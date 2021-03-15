module Typer

open Syntax


type Context = {
  currentNamespace: string list
  definitionsMap: Trie<string, Statement list>
  typeLiteralsMap: Map<Literal, int>
  anonymousInterfacesMap: Map<Class, int>
  internalModuleName: string
  unknownIdentifiers: Trie<string, Set<int>>
}

module Context =
  let ofParentNamespace (ctx: Context) : Context option =
    match ctx.currentNamespace with
    | [] -> None
    | _ :: ns -> Some { ctx with currentNamespace = ns }

let rec mapTypeInTypeParam mapping (ctx: 'Context) (tp: TypeParam) =
  { tp with
      extends = Option.map (mapping ctx) tp.extends
      defaultType = Option.map (mapping ctx) tp.defaultType }

and mapTypeInArg mapping ctx (arg: Choice<FieldLike, Type>) =
  match arg with
  | Choice1Of2 a -> mapTypeInFieldLike mapping ctx a |> Choice1Of2
  | Choice2Of2 t -> mapping ctx t |> Choice2Of2

and mapTypeInFuncType mapping (ctx: 'Context) f =
  { f with
      returnType = mapping ctx f.returnType
      args = List.map (mapTypeInArg mapping ctx) f.args }

and mapTypeInClass mapping (ctx: 'Context) (c: Class) : Class =
  let mapMember = function
    | Field (f, m, tps) -> Field (mapTypeInFieldLike mapping ctx f, m, List.map (mapTypeInTypeParam mapping ctx) tps)
    | FunctionInterface (f, tps) -> FunctionInterface (mapTypeInFuncType mapping ctx f, List.map (mapTypeInTypeParam mapping ctx) tps)
    | Indexer (f, m) -> Indexer (mapTypeInFuncType mapping ctx f, m)
    | Constructor (c, tps) -> Constructor ({ c with args = List.map (mapTypeInArg mapping ctx) c.args }, List.map (mapTypeInTypeParam mapping ctx) tps)
    | Getter f -> Getter (mapTypeInFieldLike mapping ctx f)
    | Setter f -> Setter (mapTypeInFieldLike mapping ctx f)
    | New (f, tps) -> New (mapTypeInFuncType mapping ctx f, List.map (mapTypeInTypeParam mapping ctx) tps)
    | Method (name, f, tps) -> Method (name, mapTypeInFuncType mapping ctx f, List.map (mapTypeInTypeParam mapping ctx) tps)
  { c with
      implements = c.implements |> List.map (mapping ctx)
      members = c.members |> List.map (fun (a, m) -> a, mapMember m)
      typeParams = c.typeParams |> List.map (mapTypeInTypeParam mapping ctx) }

and mapTypeInFieldLike mapping (ctx: 'Context) (fl: FieldLike) : FieldLike =
  { fl with value = mapping ctx fl.value }

let rec mapTypeInStatements mapping ctx stmts =
  let f = function
    | TypeAlias a ->
      TypeAlias {
        a with
          target = mapping ctx a.target
          typeParams = a.typeParams |> List.map (mapTypeInTypeParam mapping ctx)
      }
    | ClassDef c ->
      ClassDef (mapTypeInClass mapping ctx c)
    | EnumDef e -> EnumDef e
    | Export (i, m) -> Export (i, m)
    | Value v ->
      Value {
        v with
          typ = mapping ctx v.typ
          typeParams = v.typeParams |> List.map (mapTypeInTypeParam mapping ctx)
      }
    | Module m ->
      Module {
        m with
          statements =
            mapTypeInStatements 
              mapping
              { ctx with currentNamespace = m.name :: ctx.currentNamespace }
              m.statements 
      }
    | UnknownStatement msgo -> UnknownStatement msgo
  stmts |> List.map f

let rec substTypeVar (subst: Map<string, Type>) _ctx = function
  | TypeVar v ->
    match subst |> Map.tryFind v with
    | Some t -> t
    | None -> TypeVar v
  | Union u ->
    Union {
      types = u.types |> List.map (substTypeVar subst _ctx);
    }
  | Intersection i ->
    Intersection {
      types = i.types |> List.map (substTypeVar subst _ctx);
    }
  | Tuple ts -> Tuple (ts |> List.map (substTypeVar subst _ctx))
  | ReadonlyTuple ts -> ReadonlyTuple (ts |> List.map (substTypeVar subst _ctx))
  | AnonymousInterface c -> AnonymousInterface (mapTypeInClass (substTypeVar subst) _ctx c)
  | Function f ->
    Function
      { f with
          returnType = substTypeVar subst _ctx f.returnType;
          args = List.map (mapTypeInArg (substTypeVar subst) _ctx) f.args }
  | App (t, ts, loc) -> App (substTypeVar subst _ctx t, ts |> List.map (substTypeVar subst _ctx), loc)
  | Ident i -> Ident i | Prim p -> Prim p | TypeLiteral l -> TypeLiteral l
  | PolymorphicThis -> PolymorphicThis
  | Erased (e, loc) ->
    let e' =
      match e with
      | IndexedAccess (t1, t2) -> IndexedAccess (substTypeVar subst _ctx t1, substTypeVar subst _ctx t2)
      | TypeQuery i -> TypeQuery i
      | Keyof t -> Keyof (substTypeVar subst _ctx t)
    Erased (e', loc)
  | UnknownType msgo -> UnknownType msgo

let rec replaceAliasToFunctionWithInterface = function
  | Module m ->
    Module { m with statements = List.map replaceAliasToFunctionWithInterface m.statements }
  | TypeAlias ta ->
    match ta.target with
    | Function f ->
      ClassDef {
        name = Some ta.name
        isInterface = true
        comments = []
        accessibility = Protected
        isExported = false
        implements = []
        typeParams = ta.typeParams
        members = [ 
          { comments = []; accessibility = Public; isStatic = false },
          FunctionInterface (f, [])
        ]
      }
    | _ -> TypeAlias ta
  | x -> x

let rec assignTypeParams fn (loc: Location) (typrms: TypeParam list) (xs: 'a list) (f: TypeParam -> 'a -> 'b) (g: TypeParam -> 'b) : 'b list =
  match typrms, xs with
  | typrm :: typrms, x :: xs ->
    f typrm x :: assignTypeParams fn loc typrms xs f g
  | typrm :: typrms, [] ->
    g typrm :: assignTypeParams fn loc typrms [] f g
  | [], [] -> []
  | [], _ :: _ ->
    failwithf "assignTypeParams: too many type arguments for type '%s' at %s" (String.concat "." fn) (loc.AsString)

let createBindings fn (loc: Location) typrms ts =
  assignTypeParams fn loc typrms ts
    (fun tv ty -> tv.name, ty)
    (fun tv ->
      match tv.defaultType with
      | Some ty -> tv.name, ty
      | None ->
        failwithf "createBindings: insufficient type arguments for type '%s' at %s" (String.concat "." fn) (loc.AsString))
  |> Map.ofList

let rec mergeStatements (stmts: Statement list) =
  let mutable result : Choice<Statement, Class ref, Module ref> list = []
  let mutable intfMap = Map.empty
  let mutable nsMap = Map.empty
  for stmt in stmts do
    match stmt with
    | ClassDef i when i.isInterface ->
      match intfMap |> Map.tryFind i.name with
      | None ->
        let iref = ref i
        intfMap <- (intfMap |> Map.add i.name iref)
        result <- Choice2Of3 iref :: result
      | Some iref' ->
        let i' =
          let mapping =
            List.map2
              (fun (tp: TypeParam) (tp': TypeParam) -> tp'.name, TypeVar tp.name)
              i.typeParams (!iref').typeParams
          !iref' |> mapTypeInClass (substTypeVar (mapping |> Map.ofList)) ()
        assert (i.accessibility = i'.accessibility)
        let implements = List.distinct (i.implements @ i'.implements)
        let members = i.members @ i'.members
        let i = { i with implements = implements; members = members }
        iref' := i
    | Module n (* when n.isNamespace *) ->
      match nsMap |> Map.tryFind n.name with
      | None ->
        let nref = ref n
        nsMap <- (nsMap |> Map.add n.name nref)
        result <- Choice3Of3 nref :: result
      | Some nref' ->
        nref' := { n with statements = (!nref').statements @ n.statements }
    | stmt ->
      result <- Choice1Of3 stmt :: result
  result
  |> List.rev
  |> List.map (function
    | Choice1Of3 s -> s
    | Choice2Of3 i -> ClassDef !i
    | Choice3Of3 n ->
      Module { !n with statements = mergeStatements (!n).statements }
  )

let extractNamedDefinitions (stmts: Statement list) : Trie<string, Statement list> =
  let rec go (ns: string list) trie s =
    match s with
    | TypeAlias { erased = true }
    | Export _
    | UnknownStatement _ -> trie
    | TypeAlias { name = name; erased = false }
    | ClassDef  { name = Some name }
    | EnumDef   { name = name }
    | Value     { name = name } ->
      trie |> Trie.addOrUpdate (ns @ [name]) [s] List.append
    | ClassDef  { name = None } -> failwith "impossible_extractNamedDefinitions"
    | Module m ->
      let ns' = ns @ [m.name]
      m.statements |> List.fold (go ns') trie |> Trie.addOrUpdate ns' [Module m] List.append
  stmts |> List.fold (go []) Trie.empty

let findTypesInType (pred: Type -> Choice<bool, Type list> * 'a option) (t: Type) : 'a seq =
  let rec go_t x =
    match x with
    | App (t, ts, _) ->
      seq {
        let cont, y = pred x
        match y with Some v -> yield v | None -> ()
        match cont with
        | Choice1Of2 false -> ()
        | Choice1Of2 true ->
          yield! go_t t
          for t in ts do yield! go_t t
        | Choice2Of2 ts -> for t in ts do yield! go_t t
      }
    | Union { types = ts } | Intersection { types = ts } | Tuple ts | ReadonlyTuple ts ->
      seq {
        let cont, y = pred x
        match y with Some v -> yield v | None -> ()
        match cont with
        | Choice1Of2 false -> ()
        | Choice1Of2 true ->
          for t in ts do yield! go_t t
        | Choice2Of2 ts -> for t in ts do yield! go_t t
      }
    | Function f ->
      seq {
        let cont, y = pred x
        match y with Some v -> yield v | None -> ()
        match cont with
        | Choice1Of2 false -> ()
        | Choice1Of2 true ->
          yield! go_t f.returnType
          for a in f.args do
            match a with
            | Choice1Of2 { value = t }
            | Choice2Of2 t ->
              yield! go_t t
        | Choice2Of2 ts -> for t in ts do yield! go_t t
      }
    | Erased (IndexedAccess (t1, t2), _) ->
      seq {
        let cont, y = pred x
        match y with Some v -> yield v | None -> ()
        match cont with
        | Choice1Of2 false -> ()
        | Choice1Of2 true ->
          yield! go_t t1
          yield! go_t t2
        | Choice2Of2 ts -> for t in ts do yield! go_t t
      }
    | AnonymousInterface c ->
      let cont, y = pred x
      let xs =
        match cont with
        | Choice1Of2 false -> Seq.empty
        | Choice1Of2 true ->
          let ra = ResizeArray()
          mapTypeInClass (fun () t -> ra.Add(t); t) () c |> ignore;
          ra |> Seq.collect go_t
        | Choice2Of2 ts -> ts |> Seq.collect go_t
      Seq.append (Option.toList y) xs
    | x ->
      seq {
        let cont, y = pred x
        match y with Some v -> yield v | None -> ()
        match cont with
        | Choice1Of2 _ -> ()
        | Choice2Of2 ts -> for t in ts do yield! go_t t
      }
  go_t t

let findTypesInStatements pred (stmts: Statement list) : 'a seq =
  let rec go = function
    | TypeAlias ta ->
      seq {
        yield! findTypesInType pred ta.target;
        for tp in ta.typeParams do
          yield! [tp.defaultType; tp.extends] |> List.choose id |> Seq.collect (findTypesInType pred)
      }
    | ClassDef c -> findTypesInType pred (AnonymousInterface c)
    | Module m ->
      m.statements |> Seq.collect go
    | Value v ->
      seq {
        yield! findTypesInType pred v.typ
        for tp in v.typeParams do
          yield! [tp.defaultType; tp.extends] |> List.choose id |> Seq.collect (findTypesInType pred)
      }
    | EnumDef e ->
      e.cases |> Seq.choose (fun c -> c.value)
              |> Seq.collect (fun l -> findTypesInType pred (TypeLiteral l))
    | Export _ | UnknownStatement _ -> Seq.empty
  stmts |> Seq.collect go

let getTypeLiterals stmts =
  findTypesInStatements (function TypeLiteral l -> Choice1Of2 true, Some l | _ -> Choice1Of2 true, None) stmts |> Set.ofSeq

let getAnonymousInterfaces stmts =
  findTypesInStatements (function
    | AnonymousInterface c when Option.isNone c.name -> Choice1Of2 true, Some c
    | _ -> Choice1Of2 true, None
  ) stmts |> Set.ofSeq

let getUnknownIdentifiers stmts =
  findTypesInStatements (function
    | App (Ident {name = name; fullName = None}, ts, _) ->
      Choice2Of2 ts, Some (name, Set.singleton (List.length ts))
    | Ident { name = name; fullName = None } ->
      Choice1Of2 true, Some (name, Set.singleton (0))
    | _ -> Choice1Of2 true, None
  ) stmts |> Seq.fold (fun state (k, v) -> Trie.addOrUpdate k v Set.union state) Trie.empty

let private createRootContextForTyper internalModuleName stmts =
  let add name ty m =
    if m |> Trie.containsKey [name] then m
    else
      m |> Trie.add [name] [TypeAlias { name = name; typeParams = []; target = ty; erased = true }]
  let m =
    extractNamedDefinitions stmts
    |> add "String" (Prim String)
    |> add "Boolean" (Prim Bool)
    |> add "Number" (Prim Number)
    |> add "Object" (Prim Object)
    |> add "Function" (Prim UntypedFunction)
    |> add "Symbol" (Prim Symbol)
    |> add "RegExp" (Prim RegExp)
    |> add "Array" (Prim Array)
    |> add "ReadonlyArray" (Prim ReadonlyArray)
    |> add "BigInt" (Prim BigInt)
  {
    internalModuleName = internalModuleName
    currentNamespace = []
    definitionsMap = m
    typeLiteralsMap = Map.empty
    anonymousInterfacesMap = Map.empty
    unknownIdentifiers = Trie.empty
  }

let createRootContext (internalModuleName: string) (stmts: Statement list) : Context =
  let tlm = getTypeLiterals stmts |> Seq.mapi (fun i l -> l, i) |> Map.ofSeq
  let aim = getAnonymousInterfaces stmts |> Seq.mapi (fun i c -> c, i) |> Map.ofSeq
  let uid = getUnknownIdentifiers stmts
  { createRootContextForTyper internalModuleName stmts with
      typeLiteralsMap = tlm
      anonymousInterfacesMap = aim
      unknownIdentifiers = uid }

type FullNameLookupResult =
  | AliasName of TypeAlias
  | ClassName of Class
  | EnumName of Enum
  | EnumCaseName of string * Enum
  | ModuleName of Module
  | ValueName of Value
  | MemberName of MemberAttribute * Member
  | NotFound of string option

let rec getFullNameOfIdent (ctx: Context) (ident: IdentType) : string list option =
  let nsRev = List.rev ctx.currentNamespace
  let fullName = nsRev @ ident.name
  let onFail () =
    match Context.ofParentNamespace ctx with Some ctx -> getFullNameOfIdent ctx ident | None -> None
  match ctx.definitionsMap |> Trie.tryFind fullName with
  | Some ((TypeAlias _ | ClassDef _ | EnumDef _ | Module _ | Value _) :: _) -> Some fullName
  | None when List.length ident.name > 1 ->
    let possibleEnumName = nsRev @ (ident.name |> List.take (List.length ident.name - 1))
    let possibleEnumCaseName = ident.name |> List.last
    let rec find = function
      | EnumDef e :: _ when e.cases |> List.exists (fun c -> c.name = possibleEnumCaseName) ->
        Some (possibleEnumName @ [possibleEnumCaseName])
      | _ :: xs -> find xs
      | [] -> onFail ()
    match ctx.definitionsMap |> Trie.tryFind possibleEnumName with
    | Some xs -> find xs
    | _ -> onFail ()
  | _ -> onFail ()

let lookupFullName (ctx: Context) (fullName: string list) : FullNameLookupResult list =
  let conv = function
    | TypeAlias a -> AliasName a |> Some
    | ClassDef c -> ClassName c |> Some
    | EnumDef e -> EnumName e |> Some
    | Module m -> ModuleName m |> Some
    | Value v -> ValueName v |> Some
    | _ -> None
  let result = ctx.definitionsMap |> Trie.tryFind fullName
  [
    match result with
    | Some xs -> yield! List.choose conv xs
    | None -> ()
    let containerName = fullName |> List.take (List.length fullName - 1)
    let itemName = List.last fullName
    let containerResult = ctx.definitionsMap |> Trie.tryFind containerName
    let notFound fmt =
      Printf.ksprintf (fun s -> NotFound (Some s)) fmt
    let rec find = function
      | EnumDef e :: rest ->
        match e.cases |> List.tryFind (fun c -> c.name = itemName) with
        | Some _ -> EnumCaseName (itemName, e) :: find rest
        | None ->
          notFound "The enum '%s' does not have a case '%s" (containerName |> String.concat ".") itemName :: find rest
      | ClassDef c :: rest ->
        let result =
          c.members |> List.tryPick (fun (ma, m) ->
            match m with
            | Field (fl, _, _) | Getter fl | Setter fl when fl.name = itemName -> MemberName (ma, m) |> Some
            | Method (name, _, _) when name = itemName -> MemberName (ma, m) |> Some
            | _ -> None
          )
        match result with
        | None ->
          match c.name with
          | Some name ->
            notFound "The class '%s' does not have a member '%s'" name itemName :: find rest
          | None ->
            notFound "The anonymous interface '%s' does not have a member '%s'" (Type.pp (AnonymousInterface c)) itemName :: find rest
        | Some x -> x :: find rest
      | _ :: rest -> find rest
      | [] -> [notFound "Current context doesn't contain '%s'" (fullName |> String.concat ".")]
    match containerResult with
    | Some xs -> yield! find xs
    | None -> ()
  ]

let lookupFullNameWith ctx fullName picker =
  let result = lookupFullName ctx fullName
  match List.tryPick picker result with
  | Some x -> x
  | None ->
    match List.choose (function NotFound msg -> msg | _ -> None) result with
    | [] ->
      failwithf "error: failed to find '%s' from the context. current context doesn't contain it."
        (String.concat "." fullName)
    | errors ->
      failwithf "error: failed to find '%s' from the context. %s"
        (errors |> List.map (fun s -> "       " + s) |> String.concat System.Environment.NewLine) 

let tryLookupFullNameWith ctx fullName picker =
  lookupFullName ctx fullName |> List.tryPick picker

let rec mapIdent (mapping: Context -> IdentType -> IdentType) (ctx: Context) = function
  | Ident i -> Ident (mapping ctx i)
  | Union u -> Union { types = u.types |> List.map (mapIdent mapping ctx) }
  | Intersection i -> Intersection { types = i.types |> List.map (mapIdent mapping ctx) }
  | Tuple ts -> Tuple (ts |> List.map (mapIdent mapping ctx))
  | ReadonlyTuple ts -> ReadonlyTuple (ts |> List.map (mapIdent mapping ctx))
  | AnonymousInterface c -> AnonymousInterface (mapTypeInClass (mapIdent mapping) ctx c)
  | Function f ->
    Function
      { f with
          returnType = mapIdent mapping ctx f.returnType;
          args = List.map (mapTypeInArg (mapIdent mapping) ctx) f.args }
  | App (t, ts, loc) -> App (mapIdent mapping ctx t, ts |> List.map (mapIdent mapping ctx), loc)
  | Prim p -> Prim p | TypeLiteral l -> TypeLiteral l | TypeVar v -> TypeVar v
  | PolymorphicThis -> PolymorphicThis
  | Erased (e, loc) ->
    let e' =
      match e with
      | IndexedAccess (t1, t2) -> IndexedAccess (mapIdent mapping ctx t1, mapIdent mapping ctx t2)
      | TypeQuery i -> TypeQuery (mapping ctx i)
      | Keyof t -> Keyof (mapIdent mapping ctx t)
    Erased (e', loc)
  | UnknownType msg -> UnknownType msg

let rec mapIdentInStatements mapType mapExport (ctx: Context) (stmts: Statement list) : Statement list =
  let f = function
    | TypeAlias a ->
      TypeAlias {
        a with
          target = mapIdent mapType ctx a.target
          typeParams = a.typeParams |> List.map (mapTypeInTypeParam (mapIdent mapType) ctx)
      }
    | ClassDef c ->
      ClassDef (mapTypeInClass (mapIdent mapType) ctx c)
    | EnumDef e -> EnumDef e
    | Export (i, m) -> mapExport ctx i m
    | Value v ->
      Value {
        v with
          typ = mapIdent mapType ctx v.typ
          typeParams = v.typeParams |> List.map (mapTypeInTypeParam (mapIdent mapType) ctx)
      }
    | Module m ->
      Module {
        m with
          statements =
            mapIdentInStatements 
              mapType mapExport
              { ctx with currentNamespace = m.name :: ctx.currentNamespace }
              m.statements 
      }
    | UnknownStatement msgo -> UnknownStatement msgo
  stmts |> List.map f

let resolveIdentType (ctx: Context) (i: IdentType) : IdentType =
  match i.fullName with
  | Some _ -> i
  | None ->
    match getFullNameOfIdent ctx i with
    | Some fn -> { i with fullName = Some fn }
    | None -> i

let resolveIdentInStatements (ctx: Context) (stmts: Statement list) : Statement list =
  mapIdentInStatements
    (fun ctx i -> resolveIdentType ctx i)
    (fun ctx i m ->
      match i.fullName with
      | Some _ -> Export (i, m)
      | None ->
        match getFullNameOfIdent ctx i with
        | Some fn -> Export ({ i with fullName = Some fn }, m)
        | None -> Export (i, m)
    ) ctx stmts

let mutable private inheritMap: Map<string list, Set<Type>> = Map.empty

let rec getAllInheritancesOfType (ctx: Context) (ty: Type) : Set<Type> =
  let rec go t =
    seq {
      match t with
      | Ident { fullName = Some fn } ->
        yield! getAllInheritancesFromName ctx fn
      | App (Ident { fullName = Some fn }, ts, loc) ->
        yield!
          lookupFullName ctx fn
          |> List.tryPick (function
            | AliasName { typeParams = typrms } | ClassName { typeParams = typrms } ->
              let subst = createBindings fn loc typrms ts
              getAllInheritancesFromName ctx fn |> Seq.map (substTypeVar subst ctx) |> Some
            | _ -> None
          ) |> Option.defaultValue Seq.empty
      | Union u -> yield! Set.intersectMany (List.map go u.types)
      | Intersection i -> yield! Set.unionMany (List.map go i.types)
      | _ -> ()
    } |> Set.ofSeq
  in go ty

and getAllInheritancesFromName (ctx: Context) (className: string list) : Set<Type> =
  match inheritMap |> Map.tryFind className with
  | Some s -> s
  | None ->
    let result =
      lookupFullName ctx className |> Seq.collect (function
        | ClassName c ->
          seq {
            for t in c.implements do
              yield t
              yield! getAllInheritancesOfType ctx t
          }
        | AliasName t when not t.erased -> getAllInheritancesOfType ctx t.target |> Set.toSeq
        | _ -> Seq.empty
      ) |> Set.ofSeq
    inheritMap <- inheritMap |> Map.add className result
    result

let private createFunctionInterface typrms ft =
  {
    comments = []
    name = None
    accessibility = Public
    isInterface = true
    isExported = false
    implements = []
    typeParams = []
    members = [
      { comments = []; isStatic = false; accessibility = Public },
      FunctionInterface (ft, typrms)
    ]
  }

let rec resolveErasedTypes (ctx: Context) (stmts: Statement list) =
  let (|Dummy|) _ = []
  let rec f ctx = function
    | PolymorphicThis -> PolymorphicThis
    | Ident i -> Ident i | TypeVar v -> TypeVar v | Prim p -> Prim p | TypeLiteral l -> TypeLiteral l
    | AnonymousInterface c -> mapTypeInClass f ctx c |> AnonymousInterface
    | Union { types = types } -> Union { types = List.map (f ctx) types }
    | Intersection { types = types } -> Intersection { types = List.map (f ctx) types }
    | Tuple ts -> Tuple (List.map (f ctx) ts)
    | ReadonlyTuple ts -> ReadonlyTuple (List.map (f ctx) ts)
    | Function ft -> mapTypeInFuncType f ctx ft |> Function
    | App (t, ts, loc) -> App (f ctx t, List.map (f ctx) ts, loc)
    | Erased (e, loc) ->
      match e with
      | IndexedAccess (tobj, tindex) ->
        let memberChooser m t2 =
          match m, t2 with
          | (Field (fl, _, []) | Getter fl | Setter fl),
            TypeLiteral (LString name) when fl.name = name ->
            if fl.isOptional then Some (Union { types = [fl.value; Prim Undefined] })
            else Some fl.value
          | Constructor (_, _), TypeLiteral (LString name) when name = "constructor" ->
            Some (Prim UntypedFunction)
          | Indexer (ft, _), (Prim Number | TypeLiteral (LInt _)) -> Some ft.returnType
          | Method (name', ft, typrms), TypeLiteral (LString name) when name = name' ->
            Some (AnonymousInterface (createFunctionInterface typrms ft))
          | _, _ -> None
        let rec go t1 t2 =
          let onFail () =
            let tyText = Type.pp (Erased (IndexedAccess (t1, t2), loc))
            eprintfn "warn: cannot resolve an indexed access type '%s' at %s" tyText loc.AsString
            UnknownType (Some tyText)
          match t1, t2 with
          | Union { types = ts }, _ -> Union { types = List.map (fun t1 -> go t1 t2) ts }
          | Intersection { types = ts }, _ -> Intersection { types = List.map (fun t1 -> go t1 t2) ts }
          | AnonymousInterface c, _ ->
            let result = c.members |> List.choose (fun (ma, m) -> memberChooser m t2)
            match result with
            | [] -> onFail ()
            | [t] -> t
            | ts -> Intersection { types = ts }
          | App ((Prim Array | Prim ReadonlyArray), [t], _), Prim (Number | Any) -> f ctx t
          | (Tuple ts | ReadonlyTuple ts), TypeLiteral (LInt i) ->
            match ts |> List.tryItem i with
            | Some t -> t
            | None -> onFail ()
          | (Tuple ts | ReadonlyTuple ts), Prim (Number | Any) -> Union { types = ts }
          | (App (Ident { fullName = Some fn }, ts, loc) | (Ident { fullName = Some fn; loc = loc } & Dummy ts)), _ ->
            tryLookupFullNameWith ctx fn (function 
              | AliasName ta when not ta.erased ->
                let subst = createBindings fn loc ta.typeParams ts
                go ta.target t2 |> substTypeVar subst ctx |> Some
              | ClassName c ->
                let subst = createBindings fn loc c.typeParams ts
                go (AnonymousInterface c) t2 |> substTypeVar subst ctx |> Some
              | _ -> None
            ) |> Option.defaultWith onFail
          | _, _ -> onFail ()
        go (f ctx tobj) (f ctx tindex)
      | TypeQuery i ->
        let onFail () =
          let tyText = Type.pp (Erased (TypeQuery i, loc))
          eprintfn "warn: cannot resolve a type query '%s' at %s" tyText loc.AsString
          UnknownType (Some tyText)
        match i.fullName with
        | None -> onFail ()
        | Some fn ->
          let result typrms ty =
            match typrms, ty with
            | _ :: _, Function ft -> AnonymousInterface (createFunctionInterface typrms ft)
            | _ :: _, _ -> onFail ()
            | [], _ -> ty
          tryLookupFullNameWith ctx fn (function
            | ValueName v -> result v.typeParams v.typ |> Some
            | MemberName (_, m) ->
              match m with
              | Field (ft, _, typrms) | (Getter ft & Dummy typrms) | (Setter ft & Dummy typrms) ->
                match ft.isOptional, result typrms ft.value with
                | true, UnknownType msgo -> UnknownType msgo |> Some
                | true, t -> Union { types =  [t; Prim Undefined] } |> Some
                | false, t -> Some t
              | Method (_, ft, typrms) -> result typrms (Function ft) |> Some
              | _ -> None
            | _ -> None
          ) |> Option.defaultWith onFail
      | Keyof t ->
        let t = f ctx t
        let onFail () =
          let tyText = Type.pp t
          eprintfn "warn: cannot resolve a type operator 'keyof %s' at %s" tyText loc.AsString
          UnknownType (Some tyText) 
        let memberChooser = function
          | Field (fl, _, _) | Getter fl | Setter fl -> Set.singleton (TypeLiteral (LString fl.name))
          | Method (name, _, _) -> Set.singleton (TypeLiteral (LString name))
          | _ -> Set.empty
        let rec go = function
          | Union { types = ts } -> ts |> List.map go |> Set.intersectMany
          | Intersection { types = ts } -> ts |> List.map go |> Set.unionMany
          | AnonymousInterface i ->
            i.members |> List.map (snd >> memberChooser) |> Set.unionMany
          | App ((Prim Array | Prim ReadonlyArray), [_], _) | Tuple _ | ReadonlyTuple _ -> Set.singleton (Prim Number)
          | (App (Ident { fullName = Some fn }, ts, loc) | (Ident { fullName = Some fn; loc = loc } & Dummy ts)) ->
            tryLookupFullNameWith ctx fn (function
              | AliasName ta when not ta.erased ->
                let subst = createBindings fn loc ta.typeParams ts
                go ta.target |> Set.map (substTypeVar subst ctx) |> Some
              | ClassName c ->
                let subst = createBindings fn loc c.typeParams ts
                c.members |> List.map (snd >> memberChooser >> Set.map (substTypeVar subst ctx)) |> Set.unionMany |> Some
              | _ -> None
            ) |> Option.defaultValue Set.empty
          | _ -> Set.empty
        let types = go t
        if Set.isEmpty types then onFail ()
        else
          Union { types = Set.toList types }
    | UnknownType msgo -> UnknownType msgo
  mapTypeInStatements f ctx stmts

let rec getEnumFromUnion ctx (u: Union) : Set<Choice<EnumCase, Literal>> * Union =
  let (|Dummy|) _ = []
  let rec go t =
    seq {
      match t with
      | Union { types = types } | Intersection { types = types } -> yield! Seq.collect go types
      | (Ident { fullName = Some fn; loc = loc } & Dummy tyargs) | App (Ident { fullName = Some fn }, tyargs, loc) ->
        for x in fn |> lookupFullName ctx do
          match x with
          | AliasName a when not a.erased ->
            let bindings = createBindings fn loc a.typeParams tyargs
            yield! go (a.target |> substTypeVar bindings ())
          | EnumName e ->
            for c in e.cases do yield Choice1Of2 (Choice1Of2 c)
          | EnumCaseName (name, e) ->
            match e.cases |> List.tryFind (fun c -> c.name = name) with
            | Some c -> yield Choice1Of2 (Choice1Of2 c)
            | None -> yield Choice2Of2 t
          | _ -> yield Choice2Of2 t
      | TypeLiteral l -> yield Choice1Of2 (Choice2Of2 l)
      | _ -> yield Choice2Of2 t
    }
  let result = go (Union u)
  let e, rest = Seq.fold (fun (e, rest) -> function Choice1Of2 x -> x::e,rest | Choice2Of2 x -> e,x::rest) ([],[]) result
  Set.ofList e, { types = rest }

let getDiscriminatedFromUnion ctx (u: Union) : Map<string, Map<Literal, Type>> * Union =
  let (|Dummy|) _ = []
  
  let rec goBase onUnion onIntersection onClass onOther t =
    seq {
      match t with
      | Union u -> yield! onUnion u
      | Intersection i -> yield! onIntersection i
      | AnonymousInterface i ->
        yield onClass t i
      | (Ident { fullName = Some fn; loc = loc } & Dummy ts) | App (Ident {fullName = Some fn}, ts, loc) ->
        for x in lookupFullName ctx fn do
          match x with
          | AliasName a ->
            if List.isEmpty ts then
              yield! goBase onUnion onIntersection onClass onOther a.target
            else
              yield! goBase onUnion onIntersection onClass onOther (App (a.target, ts, UnknownLocation))
          | ClassName c ->
            if List.isEmpty ts then
              yield onClass t c
            else
              let bindings = createBindings fn loc c.typeParams ts
              yield onClass t (c |> mapTypeInClass (substTypeVar bindings) ())
          | _ -> yield onOther t
      | _ -> yield onOther t
   }

  let rec getLiteralFieldsFromClass (c: Class) =
    let inheritedFields =
      if not c.isInterface then Set.empty
      else
        c.implements
        |> Seq.collect (fun t ->
          go t |> Seq.choose (function
            | Choice1Of2 (_, fields) -> Some fields
            | Choice2Of2 _ -> None)
          )
        |> Set.unionMany
        
    c.members |> List.collect (fun (_, m) ->
      match m with
      | Field (fl, _, []) ->
        let rec go t =
          match t with
          | TypeLiteral l -> [fl.name, l]
          | (Ident { fullName = Some fn; loc = loc } & Dummy ts) | App (Ident { fullName = Some fn }, ts, loc) ->
            tryLookupFullNameWith ctx fn (function
              | EnumCaseName (cn, e) ->
                match e.cases |> List.tryFind (fun c -> c.name = cn) with
                | Some { value = Some v } -> Some [fl.name, v]
                | _ -> None
              | AliasName a when not a.erased ->
                let bindings = createBindings fn loc a.typeParams ts
                go (a.target |> substTypeVar bindings ()) |> Some
              | _ -> None
            ) |> Option.defaultValue []
          | _ -> []
        go fl.value
      | _ -> []) |> Set.ofList |> Set.union inheritedFields

  and goIntersection t : Set<string * Literal> =
    goBase
      (fun u -> u.types |> List.map goIntersection |> Set.intersectMany |> Seq.singleton)
      (fun i -> i.types |> List.map goIntersection |> Set.unionMany |> Seq.singleton)
      (fun _ i -> getLiteralFieldsFromClass i)
      (fun _ -> Set.empty)
      t
    |> Set.unionMany

  and go t : Choice<Type * Set<string * Literal>, Type> seq =
    goBase
      (fun u -> Seq.collect go u.types)
      (fun i ->
        let fields = goIntersection (Intersection i)
        if Set.isEmpty fields then Seq.singleton (Choice2Of2 (Intersection i))
        else Seq.singleton (Choice1Of2 (Intersection i, fields)))
      (fun t c ->
        let fields = getLiteralFieldsFromClass c
        if Set.isEmpty fields then Choice2Of2 t
        else Choice1Of2 (t, fields))
      (fun t -> Choice2Of2 t)
      t

  let classes, rest = Seq.fold (fun (e, rest) -> function Choice1Of2 x -> x::e,rest | Choice2Of2 x -> e,x::rest) ([],[]) (go (Union u))

  let tagDict = new System.Collections.Generic.Dictionary<string, uint32>()
  for (_, fields) in classes do
    for (name, _) in fields |> Set.toSeq do
      match tagDict.TryGetValue(name) with
      | true, i -> tagDict.[name] <- i + 1u
      | false, _ -> tagDict.[name] <- 1u

  let getBestTag (fields: Set<string * Literal>) =
    fields |> Set.toSeq |> Seq.maxBy (fun (name, _) ->
      match tagDict.TryGetValue(name) with
      | true, i -> i
      | false, _ -> 0u
    )

  let dus =
    classes
    |> List.distinct
    |> List.map (fun (t, fields) ->
      let name, value = getBestTag fields
      name, (value, t))
    |> List.groupBy fst
    |> List.map (fun (name, xs) ->
      name,
      xs |> List.map snd
         |> List.groupBy fst
         |> List.map (fun (k, xs) ->
              match xs with
              | [x] -> k, snd x
              | xs -> k, Union { types = List.map snd xs })
         |> Map.ofList)
    |> Map.ofList

  dus, { types = rest }

type TypeofableType = TNumber | TString | TBoolean | TSymbol

type ResolvedUnion = {
  caseNull: bool
  caseUndefined: bool
  typeofableTypes: Set<TypeofableType>
  caseArray: Set<Type> option
  caseEnum: Set<Choice<EnumCase, Literal>>
  discriminatedUnions: Map<string, Map<Literal, Type>>
  otherTypes: Set<Type>
}

module TypeofableType =
  let toType = function
    | TNumber -> Prim Number
    | TString -> Prim String
    | TBoolean -> Prim Bool
    | TSymbol -> Prim Symbol

module ResolvedUnion =
  let rec pp (ru: ResolvedUnion) =
    let cases = [
      if ru.caseNull then yield "null"
      if ru.caseUndefined then yield "undefined"
      for x in ru.typeofableTypes do
        yield
          match x with TNumber -> "number" | TString -> "string" | TBoolean -> "boolean" | TSymbol -> "symbol"
      match ru.caseArray with
      | Some t -> yield sprintf "array<%s>" (t |> Set.toSeq |> Seq.map Type.pp |> String.concat " | ")
      | None -> ()
      if not (Set.isEmpty ru.caseEnum) then
        let cases =
          ru.caseEnum
          |> Set.toSeq
          |> Seq.map (function
            | Choice1Of2 { name = name; value = Some value } -> sprintf "%s=%s" name (Literal.toString value)
            | Choice1Of2 { name = name; value = None } -> sprintf "%s=?" name
            | Choice2Of2 l -> Literal.toString l)
        yield sprintf "enum<%s>" (cases |> String.concat " | ")
      for k, m in ru.discriminatedUnions |> Map.toSeq do
        yield sprintf "du[%s]<%s>" k (m |> Map.toSeq |> Seq.map (snd >> Type.pp) |> String.concat ", ")
      for t in ru.otherTypes |> Set.toSeq do yield Type.pp t
    ]
    cases |> String.concat " | "

let mutable private resolveUnionMap: Map<Union, ResolvedUnion> = Map.empty

let rec resolveUnion (ctx: Context) (u: Union) : ResolvedUnion =
  match resolveUnionMap |> Map.tryFind u with
  | Some t -> t
  | None ->
    let nullOrUndefined, rest =
      u.types |> List.partition (function Prim (Null | Undefined) -> true | _ -> false)
    let caseNull = nullOrUndefined |> List.contains (Prim Null)
    let caseUndefined = nullOrUndefined |> List.contains (Prim Undefined)
    let prims, arrayTypes, rest =
      rest |> List.fold (fun (prims, ats, rest) ->
        function
        | Prim Number -> TNumber  :: prims, ats, rest
        | Prim String -> TString  :: prims, ats, rest
        | Prim Bool   -> TBoolean :: prims, ats, rest
        | Prim Symbol -> TSymbol  :: prims, ats, rest
        | App (Prim Array, [t], _) -> prims, t :: ats, rest
        | t -> prims, ats, t :: rest
      ) ([], [], [])
    let typeofableTypes = Set.ofList prims
    let caseArray =
      if List.isEmpty arrayTypes then None
      else Some (Set.ofList arrayTypes) 
    let caseEnum, rest =
      getEnumFromUnion ctx { types = rest }
    let discriminatedUnions, rest =
      getDiscriminatedFromUnion ctx rest
    let otherTypes = Set.ofList rest.types

    let result = 
      { caseNull = caseNull
        caseUndefined = caseUndefined
        typeofableTypes = typeofableTypes
        caseArray = caseArray
        caseEnum = caseEnum
        discriminatedUnions = discriminatedUnions
        otherTypes = otherTypes }
    
    resolveUnionMap <- resolveUnionMap |> Map.add u result
    result

let runAll stmts =

  let result =
    stmts |> List.map replaceAliasToFunctionWithInterface // replace alias to function type with a function interface
          |> mergeStatements // merge modules, interfaces, etc
  // build a context
  let ctx = createRootContextForTyper "Internal" result
  
  // resolve every identifier into its full name
  let result = result |> resolveIdentInStatements ctx
  // rebuild the context with the identifiers resolved to full name
  let ctx = createRootContextForTyper "Internal" result

  // resolve every indexed access type and type query
  let result = result |> resolveErasedTypes ctx
  // rebuild the context because resolbeIndexedAccessAndTypeQuery may introduce additional anonymous function interfaces
  let ctx = createRootContext "Internal" result

  ctx, result