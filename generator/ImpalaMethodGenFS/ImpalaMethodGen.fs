
module Generator

open System
open Bindings

    // Impala-specific function argument collection. 
    // We treat separately the declared explicit types (in the signature)
    // and the tokens (expressions of Var <token_name>).
    type ImpalaFunctionArgs = {
        zip_types : CSType list;
        zip_tokens : Expr list; 
        rdx_types : CSType list;
        rdx_tokens : Expr list;
        action_type : CSType;  
        action_token : Expr;
        error_type : CSType;
        error_token : Expr;
        in_types : CSType list;
    }
    
    // Shortcut to create a GH_Structure type.
    let GH_Structure types =
        Composite (Param "GH_Structure", types)

    // Take all the tokens and types and format them into an argument list.
    let ListifyArgs args =  
        let typs = args.zip_types  @ args.rdx_types  @ [args.action_type;  args.error_type ]
        let toks = args.zip_tokens @ args.rdx_tokens @ [args.action_token; args.error_token]
        List.zip typs toks
    

    //
    // HELPER FUNCTIONS
    // 
     
    /// "token" 0 returns the empty string, otherwise token1, token2, etc.
    let AppendNonZero tok d =  
       if d = 0 then "" else sprintf ("%s%d") tok d

    // Converts a number to a string with built in start-offset.
    let ParameterString offset nth =
        let letter n = Char.ConvertFromUtf32 (n + 65) in
        let converted = (letter ((nth % 13) + offset)) in
        String.replicate ((nth / 13) + 1) converted
    
    // Type parameter generating functions
    let InParamString = ParameterString 13
    let OutParamString = ParameterString 0
    let InParam n = Param (InParamString n)
    let OutParam n = Param (OutParamString n)


    //
    // GENERATING FUNCTIONS
    //

    // Construct the function arguments of an impala function given the arity of each dimension and 
    // whether to graft or not. Pity F# doesn't have ad-hoc variants.
    let FunctionArgs zip red out graft =
        let restypes out graft =
            List.init out (OutParam) |> List.map (fun t -> if graft then Array t else t)
        in
        let convert s offset i =
            let typestr = InParamString (i+offset)
            (Param typestr,Var ((s + typestr).ToLower()))
        in
        let (zip_types,zip_tokens) = List.init zip (convert "zip_"   0)   |> List.unzip   
        let (rdx_types,rdx_tokens) = List.init red (convert "redux_" zip) |> List.unzip
        let zip_strs = List.map (fun t -> GH_Structure [t]) (zip_types)
        let rdx_strs = List.map (fun t -> GH_Structure [t]) (rdx_types)
        let rdx_ls = List.map (fun t -> Composite (Param "List", [t])) rdx_types
        let res_typs = restypes out graft 
        let func_t = Composite (Param "Func", zip_types @ rdx_ls @ [TTuple (res_typs)]) 
        let err_t  = Composite (Param "ErrorChecker", zip_types @ rdx_ls)
        { 
          zip_types = zip_strs;
          zip_tokens = zip_tokens;
          rdx_types = rdx_strs;
          rdx_tokens = rdx_tokens;
          action_type = func_t;
          action_token = Var "action";
          error_type = err_t;
          error_token = Var "error";
          in_types = zip_types @ rdx_types;
        }

    // Initialize the results (var result1 = new GH_Structure<A>())
    let DeclareResultStructures n =  
        let result i =
            let var = sprintf "result%d" i in
            let typ = OutParam i in
            (DeclAssign (INFER,Var var,ConstrObject(GH_Structure [typ],[])),Var var,typ)
        in
        List.init n result |> List.unzip3
    
    // Call the max function on the count property of all tokens.
    let CallMaxCount name tokens =    
        [DeclAssign(INFER,Var name,Call("Max",List.map (fun token -> Property (token,"Count")) tokens))]
    
    // Initialize the branches extracted from BOTH zips and reduxes.
    // ex: var branchzip_t = zip_t.Branches[Math.Min(i, zip_t.Branches.Count - 1)];
    let DeclareZipBranches tokens = 
        let bzips = tokens |> List.map (fun tok -> Var ("branch" + FmtExpr tok)) // Best be variables, these.
        let zipexprs = tokens |> (List.map (fun token -> 
            Index (Property(token,"Branches"), 
                   Method (Var "Math","Min",
                       [Var "i";
                        Binop (SUB, 
                              Property (Property (token,"Branches"), "Count"), 
                              IConst 1)
                       ])
                  )
           )) in 
        List.map2 (fun btok ztok -> (DeclAssign(INFER,btok,ztok),btok)) bzips zipexprs |> List.unzip

    // Call ensure paths on a list of tree tokens.
    // ex: result1.EnsurePath(targpath);
    let EnsurePaths path_tok = 
        List.map (fun t -> Simple(Method(t,"EnsurePath",[path_tok]))) 
  
    // Call appendRange on a list of tree tokens.
    // ex: result1.AppendRange(compute.Item1,targpath);
    let AppendRanges src_tok path_tok =
        List.mapi (fun i t -> 
            let item = (sprintf ("Item%d") (i+1)) in
            Simple(Method(t,"AppendRange",[Property(src_tok,item);path_tok])))
    
    // Call our modified appendRange w/ a linq query 
    // ex: result1.AppendRange(from item in temp select item.Item1, path);
    // Don't call this when the tuple has only one item.
    let AppendRangeSelects sel_token path_token = 
        List.mapi (fun i t ->
          let item = sprintf ("Item%d") (i + 1) in
          Simple(Method(t,"AppendRange",[
            Method(sel_token,"Select",[Lambda(Var "item",Property (Var "item",item))]);
            path_token
          ]))
        )
    
    // Composed conditional expression that checks for non-zero branch lenghths.
    // ex: (branchzip_t.Count > 0 && rxbranchredux_r.Count > 0)
    let AllCountZeroExpr tokens = 
        let count_tokens = List.map (fun tk -> Binop(GT,Property(tk,"Count"),IConst 0)) tokens in
        match count_tokens with 
        | [] -> BConst true
        | x :: xs -> List.fold (fun expr tk -> Binop(AND,expr,tk)) x xs
   
    // Selects single items from zip (NOT redux) branches in the inner nested parallel loop.
    // Operates on lists of zips and branch tokens.
    // ex: T zip_tx = branchzip_t[Math.Min(branchzip_t.Count - 1, j)];
    let DeclareBranchItems zippy branchy = 
        let mapping zip brnch = 
            let zip' = match zip with
                     | Var s -> Var (s + "x")
                     | _ -> raise (Failure "Non-var token in DeclareBranchItems")
            in
            let decl = DeclAssign(INFER,zip',
                         Index (brnch, 
                           Method (Var "Math","Min",
                               [Var "j";
                                Binop(SUB, 
                                      Property (brnch, "Count"), 
                                      IConst 1)] 
                           )
                         )
                       )
            in
            (decl,zip')
        in
        List.map2 mapping zippy branchy |> List.unzip
       
    // Create the final computation expression inside the inner loop.
    // ex: error.Validate((zip_tx, rxbranchredux_u)) ? action(zip_tx, rxbranchredux_u) : (new A[0]) // OR : default
    let ComputeExpression dest tokens res_typs graft = 
        [DeclAssign(INFER,dest,
            Ternary(
               Method(Var "error","Validate",[ExpTuple tokens]),
               Call("action",tokens),
               (if graft then
                    ExpTuple (List.map (fun t -> ConstrArray(t,IConst 0)) res_typs)
               else 
                    Var "default")
            )
        )]

    // Big ol' generator.
    // Zip, Redx, Out specify the arity in each dimension
    // Graft specifies whether to graft.
    let GenerateFunction zip redx out graft =   
        let empty = [EMPTY]
        let args = FunctionArgs zip redx out graft 
        let (declresult_stms,result_toks,result_typs) = DeclareResultStructures out 
        let (declbranchzip_stms,branchzip_tokens) = DeclareZipBranches args.zip_tokens 
        let (declrdxzip_stms,rdxzip_tokens) = DeclareZipBranches args.rdx_tokens 
        let (declbritem_stms,britem_tokens) = DeclareBranchItems args.zip_tokens branchzip_tokens 

        // This is where the bindings come in: we can write code that very closely 
        // mirrors the structure of the final generated statements.
        let fn_body = (List.concat [
            declresult_stms;
            empty;

            CallMaxCount "maxbranch" ((args.zip_tokens @ args.rdx_tokens) |> List.map (fun t -> Property(t,"Branches")));
            [DeclAssign(INFER,Var "paths", Call("GetPathList", (args.zip_tokens @ args.rdx_tokens)))]
            empty;

            [Loop { 
                variable = Var "i";
                start = IConst 0; stop = Var "maxbranch"; stride = IConst 1;
                body = List.concat [
                    [DeclAssign (INFER,Var "path",Call("GetPath",[Var "paths";Var "i"]))];
                    declbranchzip_stms;
                    CallMaxCount "maxlen" branchzip_tokens;
                
                    [Loop {
                        variable = Var "j";
                        start = IConst 0; stop = Var "maxlen"; stride = IConst 1;
                        body = DeclAssign(INFER,Var "targpath",
                                Method(Var "path","AppendElement",[Var "j"])) :: 
                                EnsurePaths (Var "targpath") result_toks 
                    }]
                ]
            }];
            empty;

            [Parallel {
                variable = Var "i";
                start = IConst 0; stop = Var "maxbranch"; stride = IConst 1;
                body = List.concat [
                    [DeclAssign (INFER,Var "path",Call("GetPath",[Var "paths";Var "i"]))];
                    declbranchzip_stms;
                    declrdxzip_stms;
                
                    [Condition {
                        case = AllCountZeroExpr (branchzip_tokens @ rdxzip_tokens);  
                        _if = List.concat [
                            CallMaxCount "maxlen" branchzip_tokens;
                         
                            (if graft then
                                [Parallel { 
                                    variable = Var "j";
                                    start = IConst 0; stop = Var "maxlen"; stride = IConst 1;
                                    body = List.concat [
                                        declbritem_stms;
                                        ComputeExpression (Var "compute") (britem_tokens @ rdxzip_tokens) (result_typs) graft;
                                        [DeclAssign (INFER,Var "targpath",Method(Var "path","AppendElement",[Var "j"]))];
                                        AppendRanges (Var "compute") (Var "targpath") result_toks;
                                    ];
                                }] 
                             else
                                List.concat [
                                    [DeclAssign (INFER,Var "temp",ConstrArray(TTuple(result_typs),Var"maxlen"));
                                     Parallel {
                                        variable = Var "j"; 
                                        start = IConst 0; stop = Var "maxlen"; stride = IConst 1;
                                        body = List.concat [
                                            declbritem_stms;
                                            ComputeExpression (Index(Var "temp",Var "j")) (britem_tokens @ rdxzip_tokens) (result_typs) graft;
                                        ]
                                     }]; 
                                    AppendRangeSelects (Var "temp") (Var "path") result_toks;
                                ]
                            )
                        ]; 
                        _else = None;
                    }]

                ]
            }];
            empty;

            [Return (ExpTuple(result_toks))]
        ]) 
    
        in
        // Throw it all together.
        {
          modifiers = ["public";"static"];
          name = sprintf "%s%sx%s%d" (AppendNonZero "Zip" zip) (AppendNonZero "Red" redx) (if graft then "Graft" else "") out;
          typ = TTuple (List.map (fun t -> GH_Structure [t]) result_typs);
          args = ListifyArgs args 
          parameters = args.in_types
          constraints = List.map (fun t -> (t,Param "IGH_Goo")) args.in_types;
          body = fn_body;
        }    
