// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

/// Implements a set of checks on the TAST for a file that can only be performed after type inference
/// is complete.
module internal FSharp.Compiler.PostTypeCheckSemanticChecks

open System
open System.Collections.Generic

open Internal.Utilities.Collections
open Internal.Utilities.Library
open Internal.Utilities.Library.Extras
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.AccessibilityLogic
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Features
open FSharp.Compiler.Infos
open FSharp.Compiler.InfoReader
open FSharp.Compiler.Syntax
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.SyntaxTreeOps
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeBasics
open FSharp.Compiler.TypedTreeOps
open FSharp.Compiler.TypeHierarchy
open FSharp.Compiler.TypeRelations
open Import

//--------------------------------------------------------------------------
// NOTES: reraise safety checks
//--------------------------------------------------------------------------

// "rethrow may only occur with-in the body of a catch handler".
//   -- Section 4.23. Part III. CLI Instruction Set. ECMA Draft 2002.
//
//   1. reraise() calls are converted to TOp.Reraise in the type checker.
//   2. any remaining reraise val_refs will be first class uses. These are trapped.
//   3. The freevars track free TOp.Reraise (they are bound (cleared) at try-catch handlers).
//   4. An outermost expression is not contained in a try-catch handler.
//      These may not have unbound rethrows.
//      Outermost expressions occur at:
//      * module bindings.
//      * attribute arguments.
//      * Any more? What about fields of a static class?
//   5. A lambda body (from lambda-expression or method binding) will not occur under a try-catch handler.
//      These may not have unbound rethrows.
//   6. All other constructs are assumed to generate IL code sequences.
//      For correctness, this claim needs to be justified.
//
//   Informal justification:
//   If a reraise occurs, then it is minimally contained by either:
//     a) a try-catch - accepted.
//     b) a lambda expression - rejected.
//     c) none of the above - rejected as when checking outmost expressions.

let PostInferenceChecksStackGuardDepth = GetEnvInteger "FSHARP_PostInferenceChecks" 50

//--------------------------------------------------------------------------
// check environment
//--------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type Resumable =
      | None
      /// Indicates we are expecting resumable code (the body of a ResumableCode delegate or
      /// the body of the MoveNextMethod for a state machine)
      ///   -- allowed: are we inside the 'then' branch of an 'if __useResumableCode then ...'
      ///      for a ResumableCode delegate.
      | ResumableExpr of allowed: bool

type env =
    {
      /// The bound type parameter names in scope
      boundTyparNames: string list

      /// The bound type parameters in scope
      boundTypars: TyparMap<unit>

      /// The set of arguments to this method/function
      argVals: ValMap<unit>

      /// "module remap info", i.e. hiding information down the signature chain, used to compute what's hidden by a signature
      sigToImplRemapInfo: (Remap * SignatureHidingInfo) list

      /// Are we in a quotation?
      quote : bool

      /// Are we under [<ReflectedDefinition>]?
      reflect : bool

      /// Are we in an extern declaration?
      external : bool

      /// Current return scope of the expr.
      returnScope : int

      /// Are we in an app expression (Expr.App)?
      isInAppExpr: bool

      /// Are we expecting a  resumable code block etc
      resumableCode: Resumable
    }

    override _.ToString() = "<env>"

let BindTypar env (tp: Typar) =
    { env with
         boundTyparNames = tp.Name :: env.boundTyparNames
         boundTypars = env.boundTypars.Add (tp, ()) }

let BindTypars g env (tps: Typar list) =
    let tps = NormalizeDeclaredTyparsForEquiRecursiveInference g tps
    if isNil tps then env else
    // Here we mutate to provide better names for generalized type parameters
    let nms = PrettyTypes.PrettyTyparNames (fun _ -> true) env.boundTyparNames tps
    PrettyTypes.AssignPrettyTyparNames tps nms
    List.fold BindTypar env tps

/// Set the set of vals which are arguments in the active lambda. We are allowed to return
/// byref arguments as byref returns.
let BindArgVals env (vs: Val list) =
    { env with argVals = ValMap.OfList (List.map (fun v -> (v, ())) vs) }

[<AutoOpen>]
module Limit =

    [<Flags>]
    type LimitFlags =
        | None                          = 0b00000
        | ByRef                         = 0b00001
        | ByRefOfSpanLike               = 0b00011
        | ByRefOfStackReferringSpanLike = 0b00101
        | SpanLike                      = 0b01000
        | StackReferringSpanLike        = 0b10000

    [<Struct>]
    type Limit =
        {
            scope: int
            flags: LimitFlags
        }

        member this.IsLocal = this.scope >= 1

    /// Check if the limit has the target limit.
    let inline HasLimitFlag targetLimit (limit: Limit) =
        limit.flags &&& targetLimit = targetLimit

    let NoLimit = { scope = 0; flags = LimitFlags.None }

    let CombineTwoLimits limit1 limit2 =
        let isByRef1 = HasLimitFlag LimitFlags.ByRef limit1
        let isByRef2 = HasLimitFlag LimitFlags.ByRef limit2
        let isStackSpan1 = HasLimitFlag LimitFlags.StackReferringSpanLike limit1
        let isStackSpan2 = HasLimitFlag LimitFlags.StackReferringSpanLike limit2
        let isLimited1 = isByRef1 || isStackSpan1
        let isLimited2 = isByRef2 || isStackSpan2

        // A limit that has a stack referring span-like but not a by-ref,
        //   we force the scope to 1. This is to handle call sites
        //   that return a by-ref and have stack referring span-likes as arguments.
        //   This is to ensure we can only prevent out of scope at the method level rather than visibility.
        let limit1 =
            if isStackSpan1 && not isByRef1 then
                { limit1 with scope = 1 }
            else
                limit1

        let limit2 =
            if isStackSpan2 && not isByRef2 then
                { limit2 with scope = 1 }
            else
                limit2

        match isLimited1, isLimited2 with
        | false, false ->
            { scope = 0; flags = limit1.flags ||| limit2.flags }
        | true, true ->
            { scope = Math.Max(limit1.scope, limit2.scope); flags = limit1.flags ||| limit2.flags }
        | true, false ->
            { limit1 with flags = limit1.flags ||| limit2.flags }
        | false, true ->
            { limit2 with flags = limit1.flags ||| limit2.flags }

    let CombineLimits limits =
        (NoLimit, limits)
        ||> List.fold CombineTwoLimits

type cenv =
    { boundVals: Dictionary<Stamp, int> // really a hash set

      limitVals: Dictionary<Stamp, Limit>

      mutable potentialUnboundUsesOfVals: StampMap<range>

      mutable anonRecdTypes: StampMap<AnonRecdTypeInfo>

      stackGuard: StackGuard

      g: TcGlobals

      amap: Import.ImportMap

      /// For reading metadata
      infoReader: InfoReader

      internalsVisibleToPaths : CompilationPath list

      denv: DisplayEnv

      viewCcu : CcuThunk

      reportErrors: bool

      isLastCompiland : bool*bool

      isInternalTestSpanStackReferring: bool

      // outputs
      mutable usesQuotations: bool

      mutable entryPointGiven: bool

      /// Callback required for quotation generation
      tcVal: ConstraintSolver.TcValF }

    override x.ToString() = "<cenv>"

/// Check if the value is an argument of a function
let IsValArgument env (v: Val) =
    env.argVals.ContainsVal v

/// Check if the value is a local, not an argument of a function.
let IsValLocal env (v: Val) =
    v.ValReprInfo.IsNone && not (IsValArgument env v)

/// Get the limit of the val.
let GetLimitVal cenv env m (v: Val) =
    let limit =
        match cenv.limitVals.TryGetValue v.Stamp with
        | true, limit -> limit
        | _ ->
            if IsValLocal env v then
                { scope = 1; flags = LimitFlags.None }
            else
                NoLimit

    if isSpanLikeTy cenv.g m v.Type then
        // The value is a limited Span or might have become one through mutation
        let isMutable = v.IsMutable && cenv.isInternalTestSpanStackReferring
        let isLimited = HasLimitFlag LimitFlags.StackReferringSpanLike limit

        if isMutable || isLimited then
            { limit with flags = LimitFlags.StackReferringSpanLike }
        else
            { limit with flags = LimitFlags.SpanLike }

    elif isByrefTy cenv.g v.Type then
        let isByRefOfSpanLike = isSpanLikeTy cenv.g m (destByrefTy cenv.g v.Type)

        if isByRefOfSpanLike then
            if HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limit then
                { limit with flags = LimitFlags.ByRefOfStackReferringSpanLike }
            else
                { limit with flags = LimitFlags.ByRefOfSpanLike }
        else
            { limit with flags = LimitFlags.ByRef }

    else
        { limit with flags = LimitFlags.None }

/// Get the limit of the val by reference.
let GetLimitValByRef cenv env m v =
    let limit = GetLimitVal cenv env m v

    let scope =
        // Getting the address of an argument will always be a scope of 1.
        if IsValArgument env v then 1
        else limit.scope

    let flags =
        if HasLimitFlag LimitFlags.StackReferringSpanLike limit then
            LimitFlags.ByRefOfStackReferringSpanLike
        elif HasLimitFlag LimitFlags.SpanLike limit then
            LimitFlags.ByRefOfSpanLike
        else
            LimitFlags.ByRef

    { scope = scope; flags = flags }

let LimitVal cenv (v: Val) limit =
    if not v.IgnoresByrefScope then
        cenv.limitVals[v.Stamp] <- limit

let BindVal cenv env (v: Val) =
    //printfn "binding %s..." v.DisplayName
    let alreadyDone = cenv.boundVals.ContainsKey v.Stamp
    cenv.boundVals[v.Stamp] <- 1

    let topLevelBindingHiddenBySignatureFile () =
        let parentHasSignatureFile () =
            match v.TryDeclaringEntity with
            | ParentNone -> false
            | Parent p ->
                match p.TryDeref with
                | ValueNone -> false
                | ValueSome e -> e.HasSignatureFile

        v.IsModuleBinding && not v.HasSignatureFile && parentHasSignatureFile ()

    if not env.external &&
       not alreadyDone &&
       cenv.reportErrors &&
       not v.HasBeenReferenced &&
       (not v.IsCompiledAsTopLevel || topLevelBindingHiddenBySignatureFile ()) &&
       not (v.DisplayName.StartsWithOrdinal("_")) &&
       not v.IsCompilerGenerated then

        if v.IsCtorThisVal then
            warning (Error(FSComp.SR.chkUnusedThisVariable v.DisplayName, v.Range))
        else
            warning (Error(FSComp.SR.chkUnusedValue v.DisplayName, v.Range))

let BindVals cenv env vs = List.iter (BindVal cenv env) vs

let RecordAnonRecdInfo cenv (anonInfo: AnonRecdTypeInfo) =
    if not (cenv.anonRecdTypes.ContainsKey anonInfo.Stamp) then
         cenv.anonRecdTypes <- cenv.anonRecdTypes.Add(anonInfo.Stamp, anonInfo)

//--------------------------------------------------------------------------
// approx walk of type
//--------------------------------------------------------------------------

/// Represents the container for nester type instantions, carrying information about the parent (generic type) and data about corresponding generic typar definition.
/// For current use, IlGenericParameterDef was enough. For other future use cases, conversion into F# Typar might be needed.
type TypeInstCtx =
    | NoInfo
    | IlGenericInst of parent:TyconRef * genericArg:ILGenericParameterDef
    | TyparInst of parent:TyconRef
    | TopLevelAllowingByRef

    with member x.TyparAllowsRefStruct() =
                        match x with
                        | IlGenericInst(_,ilTypar) -> ilTypar.HasAllowsRefStruct
                        | _ -> false

let rec CheckTypeDeep (cenv: cenv) (visitTy, visitTyconRefOpt, visitAppTyOpt, visitTraitSolutionOpt, visitTyparOpt as f) (g: TcGlobals) env (typeInstParentOpt:TypeInstCtx) ty =
    // We iterate the _solved_ constraints as well, to pick up any record of trait constraint solutions
    // This means we walk _all_ the constraints _everywhere_ in a type, including
    // those attached to _solved_ type variables. This is used by PostTypeCheckSemanticChecks to detect uses of
    // values as solutions to trait constraints and determine if inference has caused the value to escape its scope.
    // The only record of these solutions is in the _solved_ constraints of types.
    // In an ideal world we would, instead, record the solutions to these constraints as "witness variables" in expressions,
    // rather than solely in types.
    match ty with
    | TType_var (tp, _) when tp.Solution.IsSome ->
        for cx in tp.Constraints do
            match cx with
            | TyparConstraint.MayResolveMember(TTrait(solution=soln), _) ->
                 match visitTraitSolutionOpt, soln.Value with
                 | Some visitTraitSolution, Some sln -> visitTraitSolution sln
                 | _ -> ()
            | _ -> ()
    | _ -> ()

    let ty =
        if g.compilingFSharpCore then
            match stripTyparEqns ty with
            // When compiling FSharp.Core, do not strip type equations at this point if we can't dereference a tycon.
            | TType_app (tcref, _, _) when not tcref.CanDeref -> ty
            | _ -> stripTyEqns g ty
        else
            stripTyEqns g ty
    visitTy ty

    match ty with
    | TType_forall (tps, body) ->
        let env = BindTypars g env tps
        CheckTypeDeep cenv f g env typeInstParentOpt body
        tps |> List.iter (fun tp -> tp.Constraints |> List.iter (CheckTypeConstraintDeep cenv f g env))

    | TType_measure _ -> ()

    | TType_app (tcref, tinst, _) ->
        match visitTyconRefOpt with
        | Some visitTyconRef -> visitTyconRef typeInstParentOpt tcref
        | None -> ()

        // If it's a 'byref<'T>', don't check 'T as an inner. This allows byref<Span<'T>>.
        // 'byref<byref<'T>>' is invalid and gets checked in visitAppTy.
        //if isByrefTyconRef g tcref then
        //    CheckTypesDeepNoInner cenv f g env tinst

        if tcref.CanDeref && tcref.IsILTycon && tinst.Length = tcref.ILTyconRawMetadata.GenericParams.Length then
            (tinst,tcref.ILTyconRawMetadata.GenericParams)
            ||> List.iter2 (fun ty ilGenericParam ->
                let typeInstParent = IlGenericInst(tcref, ilGenericParam)
                CheckTypeDeep cenv f g env typeInstParent ty)
        else
            let parentRef = TyparInst(tcref)
            for ty in tinst do
                CheckTypeDeep cenv f g env parentRef ty

        match visitAppTyOpt with
        | Some visitAppTy -> visitAppTy (tcref, tinst)
        | None -> ()

    | TType_anon (anonInfo, tys) ->
        RecordAnonRecdInfo cenv anonInfo
        CheckTypesDeep cenv f g env tys

    | TType_ucase (_, tinst) ->
        CheckTypesDeep cenv f g env tinst

    | TType_tuple (_, tys) ->
        CheckTypesDeep cenv f g env tys

    | TType_fun (s, t, _) ->
        CheckTypeDeep cenv f g env NoInfo s
        CheckTypeDeep cenv f g env NoInfo t

    | TType_var (tp, _) ->
          if not tp.IsSolved then
              match visitTyparOpt with
              | None -> ()
              | Some visitTyar ->
                    visitTyar (env, tp)

and CheckTypesDeep cenv f g env tys =
    for ty in tys do
        CheckTypeDeep cenv f g env NoInfo ty

and CheckTypeConstraintDeep cenv f g env x =
     match x with
     | TyparConstraint.CoercesTo(ty, _) -> CheckTypeDeep cenv f g env NoInfo ty
     | TyparConstraint.MayResolveMember(traitInfo, _) -> CheckTraitInfoDeep cenv f g env traitInfo
     | TyparConstraint.DefaultsTo(_, ty, _) -> CheckTypeDeep cenv f g env NoInfo ty
     | TyparConstraint.SimpleChoice(tys, _) -> CheckTypesDeep cenv f g env tys
     | TyparConstraint.IsEnum(underlyingTy, _) -> CheckTypeDeep cenv f g env NoInfo underlyingTy
     | TyparConstraint.IsDelegate(argTys, retTy, _) -> CheckTypeDeep cenv f g env NoInfo argTys; CheckTypeDeep cenv f g env NoInfo retTy
     | TyparConstraint.SupportsComparison _
     | TyparConstraint.SupportsEquality _
     | TyparConstraint.SupportsNull _
     | TyparConstraint.NotSupportsNull _
     | TyparConstraint.IsNonNullableStruct _
     | TyparConstraint.IsUnmanaged _
     | TyparConstraint.AllowsRefStruct _
     | TyparConstraint.IsReferenceType _
     | TyparConstraint.RequiresDefaultConstructor _ -> ()

and CheckTraitInfoDeep cenv (_, _, _, visitTraitSolutionOpt, _ as f) g env traitInfo =
    CheckTypesDeep cenv f g env traitInfo.SupportTypes
    CheckTypesDeep cenv f g env traitInfo.CompiledObjectAndArgumentTypes
    Option.iter (CheckTypeDeep cenv f g env NoInfo ) traitInfo.CompiledReturnType
    match visitTraitSolutionOpt, traitInfo.Solution with
    | Some visitTraitSolution, Some sln -> visitTraitSolution sln
    | _ -> ()

/// Check for byref-like types
let CheckForByrefLikeType cenv env m ty check =
    CheckTypeDeep cenv (ignore, Some (fun ctx tcref -> if (isByrefLikeTyconRef cenv.g m tcref && not(ctx.TyparAllowsRefStruct())) then check()),  None, None, None) cenv.g env NoInfo ty

/// Check for byref types
let CheckForByrefType cenv env ty check =
    CheckTypeDeep cenv (ignore, Some (fun _ctx tcref -> if isByrefTyconRef cenv.g tcref then check()),  None, None, None) cenv.g env NoInfo ty

/// check captures under lambdas
///
/// This is the definition of what can/can't be free in a lambda expression. This is checked at lambdas OR TBind(v, e) nodes OR TObjExprMethod nodes.
/// For TBind(v, e) nodes we may know an 'arity' which gives as a larger set of legitimate syntactic arguments for a lambda.
/// For TObjExprMethod(v, e) nodes we always know the legitimate syntactic arguments.
let CheckEscapes cenv allowProtected m syntacticArgs body = (* m is a range suited to error reporting *)
    if cenv.reportErrors then
        let cantBeFree (v: Val) =
           // If v is a syntactic argument, then it can be free since it was passed in.
           // The following cannot be free:
           //   a) BaseVal can never escape.
           //   b) Byref typed values can never escape.
           // Note that: Local mutables can be free, as they will be boxed later.

           // These checks must correspond to the tests governing the error messages below.
           (v.IsBaseVal || isByrefLikeTy cenv.g m v.Type) &&
           not (ListSet.contains valEq v syntacticArgs)

        let frees = freeInExpr (CollectLocalsWithStackGuard()) body
        let fvs   = frees.FreeLocals

        if not allowProtected && frees.UsesMethodLocalConstructs  then
            errorR(Error(FSComp.SR.chkProtectedOrBaseCalled(), m))
        elif Zset.exists cantBeFree fvs then
            let v =  List.find cantBeFree (Zset.elements fvs)

            // byref error before mutable error (byrefs are mutable...).
            if (isByrefLikeTy cenv.g m v.Type) then
                // Inner functions are not guaranteed to compile to method with a predictable arity (number of arguments).
                // As such, partial applications involving byref arguments could lead to closures containing byrefs.
                // For safety, such functions are assumed to have no known arity, and so cannot accept byrefs.
                errorR(Error(FSComp.SR.chkByrefUsedInInvalidWay(v.DisplayName), m))

            elif v.IsBaseVal then
                errorR(Error(FSComp.SR.chkBaseUsedInInvalidWay(), m))

            else
                // Should be dead code, unless governing tests change
                errorR(InternalError(FSComp.SR.chkVariableUsedInInvalidWay(v.DisplayName), m))
        Some frees
    else
        None


/// Check type access
let AccessInternalsVisibleToAsInternal thisCompPath internalsVisibleToPaths access =
    // Each internalsVisibleToPath is a compPath for the internals of some assembly.
    // Replace those by the compPath for the internals of this assembly.
    // This makes those internals visible here, but still internal. Bug://3737
    (access, internalsVisibleToPaths) ||> List.fold (fun access internalsVisibleToPath ->
        accessSubstPaths (thisCompPath, internalsVisibleToPath) access)


let CheckTypeForAccess (cenv: cenv) env objName valAcc m ty =
    if cenv.reportErrors then

        let visitType ty =
            // We deliberately only check the fully stripped type for accessibility,
            // because references to private type abbreviations are permitted
            match tryTcrefOfAppTy cenv.g ty with
            | ValueNone -> ()
            | ValueSome tcref ->
                let thisCompPath = compPathOfCcu cenv.viewCcu
                let tyconAcc = tcref.Accessibility |> AccessInternalsVisibleToAsInternal thisCompPath cenv.internalsVisibleToPaths
                if isLessAccessible tyconAcc valAcc then
                    errorR(Error(FSComp.SR.chkTypeLessAccessibleThanType(tcref.DisplayName, (objName())), m))

        CheckTypeDeep cenv (visitType, None, None, None, None) cenv.g env NoInfo ty

let WarnOnWrongTypeForAccess (cenv: cenv) env objName valAcc m ty =
    if cenv.reportErrors then

        let visitType ty =
            // We deliberately only check the fully stripped type for accessibility,
            // because references to private type abbreviations are permitted
            match tryTcrefOfAppTy cenv.g ty with
            | ValueNone -> ()
            | ValueSome tcref ->
                let thisCompPath = compPathOfCcu cenv.viewCcu
                let tyconAcc = tcref.Accessibility |> AccessInternalsVisibleToAsInternal thisCompPath cenv.internalsVisibleToPaths
                if isLessAccessible tyconAcc valAcc then
                    let errorText = FSComp.SR.chkTypeLessAccessibleThanType(tcref.DisplayName, (objName())) |> snd
                    let warningText = errorText + Environment.NewLine + FSComp.SR.tcTypeAbbreviationsCheckedAtCompileTime()
                    warning(ObsoleteDiagnostic(false, None, Some warningText, None, m))

        CheckTypeDeep cenv (visitType, None, None, None, None) cenv.g env NoInfo ty

/// Indicates whether a byref or byref-like type is permitted at a particular location
[<RequireQualifiedAccess>]
type PermitByRefType =
    /// Don't permit any byref or byref-like types
    | None

    /// Don't permit any byref or byref-like types on inner types.
    | NoInnerByRefLike

    /// Permit only a Span or IsByRefLike type
    | SpanLike

    /// Permit all byref and byref-like types
    | All


/// Indicates whether an address-of operation is permitted at a particular location
[<RequireQualifiedAccess>]
type PermitByRefExpr =
    /// Permit a tuple of arguments where elements can be byrefs
    | YesTupleOfArgs of int

    /// Context allows for byref typed expr.
    | Yes

    /// Context allows for byref typed expr, but the byref must be returnable
    | YesReturnable

    /// Context allows for byref typed expr, but the byref must be returnable and a non-local
    | YesReturnableNonLocal

    /// General (address-of expr and byref values not allowed)
    | No

    member ctxt.Disallow =
        match ctxt with
        | PermitByRefExpr.Yes
        | PermitByRefExpr.YesReturnable
        | PermitByRefExpr.YesReturnableNonLocal -> false
        | _ -> true

    member ctxt.PermitOnlyReturnable =
        match ctxt with
        | PermitByRefExpr.YesReturnable
        | PermitByRefExpr.YesReturnableNonLocal -> true
        | _ -> false

    member ctxt.PermitOnlyReturnableNonLocal =
        match ctxt with
        | PermitByRefExpr.YesReturnableNonLocal -> true
        | _ -> false

let inline IsLimitEscapingScope env (ctxt: PermitByRefExpr) limit =
    (limit.scope >= env.returnScope || (limit.IsLocal && ctxt.PermitOnlyReturnableNonLocal))

let mkArgsPermit n =
    if n=1 then PermitByRefExpr.Yes
    else PermitByRefExpr.YesTupleOfArgs n

/// Work out what byref-values are allowed at input positions to named F# functions or members
let mkArgsForAppliedVal isBaseCall (vref: ValRef) argsl =
    match vref.ValReprInfo with
    | Some valReprInfo ->
        let argArities = valReprInfo.AritiesOfArgs
        let argArities = if isBaseCall && argArities.Length >= 1 then List.tail argArities else argArities
        // Check for partial applications: arguments to partial applications don't get to use byrefs
        if List.length argsl >= argArities.Length then
            List.map mkArgsPermit argArities
        else
            []
    | None -> []

/// Work out what byref-values are allowed at input positions to functions
let rec mkArgsForAppliedExpr isBaseCall argsl x =
    match stripDebugPoints (stripExpr x) with
    // recognise val
    | Expr.Val (vref, _, _) -> mkArgsForAppliedVal isBaseCall vref argsl
    // step through instantiations
    | Expr.App (f, _fty, _tyargs, [], _) -> mkArgsForAppliedExpr isBaseCall argsl f
    // step through subsumption coercions
    | Expr.Op (TOp.Coerce, _, [f], _) -> mkArgsForAppliedExpr isBaseCall argsl f
    | _  -> []

/// Check types occurring in the TAST.
let CheckTypeAux permitByRefLike (cenv: cenv) env m ty onInnerByrefError =
    if cenv.reportErrors then
        let visitTyar (env, tp) =
          if not (env.boundTypars.ContainsKey tp) then
             if tp.IsCompilerGenerated then
               errorR (Error(FSComp.SR.checkNotSufficientlyGenericBecauseOfScopeAnon(), m))
             else
               errorR (Error(FSComp.SR.checkNotSufficientlyGenericBecauseOfScope(tp.DisplayName), m))

        let visitTyconRef (ctx:TypeInstCtx) tcref =
            let checkInner() =
                match ctx with
                | TopLevelAllowingByRef -> false
                | TyparInst(parentTcRef)
                | IlGenericInst(parentTcRef,_) when isByrefTyconRef cenv.g parentTcRef -> false
                | _ -> true

            let isInnerByRefLike() = checkInner() && isByrefLikeTyconRef cenv.g m tcref

            let permitByRefLike =
                if ctx.TyparAllowsRefStruct() then PermitByRefType.All else permitByRefLike


            match permitByRefLike with
            | PermitByRefType.None when isByrefLikeTyconRef cenv.g m tcref ->
                errorR(Error(FSComp.SR.chkErrorUseOfByref(), m))
            | PermitByRefType.NoInnerByRefLike when isInnerByRefLike() ->
                onInnerByrefError ()
            | PermitByRefType.SpanLike when isByrefTyconRef cenv.g tcref || isInnerByRefLike() ->
                onInnerByrefError ()
            | _ -> ()

            if tyconRefEq cenv.g cenv.g.system_Void_tcref tcref then
                errorR(Error(FSComp.SR.chkSystemVoidOnlyInTypeof(), m))

        // check if T contains byref types in case of byref<T>
        let visitAppTy (tcref, tinst) =
            if isByrefLikeTyconRef cenv.g m tcref then
                let visitType ty0 =
                    match tryTcrefOfAppTy cenv.g ty0 with
                    | ValueNone -> ()
                    | ValueSome tcref2 ->
                        if isByrefTyconRef cenv.g tcref2 then
                            errorR(Error(FSComp.SR.chkNoByrefsOfByrefs(NicePrint.minimalStringOfType cenv.denv ty), m))
                CheckTypesDeep cenv (visitType, None, None, None, None) cenv.g env tinst

        let visitTraitSolution info =
            match info with
            | FSMethSln(_, vref, _, _) ->
               //printfn "considering %s..." vref.DisplayName
               if valRefInThisAssembly cenv.g.compilingFSharpCore vref && not (cenv.boundVals.ContainsKey(vref.Stamp)) then
                   //printfn "recording %s..." vref.DisplayName
                   cenv.potentialUnboundUsesOfVals <- cenv.potentialUnboundUsesOfVals.Add(vref.Stamp, m)
            | _ -> ()

        let initialCtx =
            match permitByRefLike with
            | PermitByRefType.SpanLike
            | PermitByRefType.NoInnerByRefLike -> TopLevelAllowingByRef
            | _ -> NoInfo

        CheckTypeDeep cenv (ignore, Some visitTyconRef, Some visitAppTy, Some visitTraitSolution, Some visitTyar) cenv.g env initialCtx ty

let CheckType permitByRefLike cenv env m ty =
    CheckTypeAux permitByRefLike cenv env m ty (fun () -> errorR(Error(FSComp.SR.chkErrorUseOfByref(), m)))

/// Check types occurring in TAST (like CheckType) and additionally reject any byrefs.
/// The additional byref checks are to catch "byref instantiations" - one place were byref are not permitted.
let CheckTypeNoByrefs (cenv: cenv) env m ty = CheckType PermitByRefType.None cenv env m ty

/// Check types occurring in TAST but allow a Span or similar
let CheckTypePermitSpanLike (cenv: cenv) env m ty = CheckType PermitByRefType.SpanLike cenv env m ty

/// Check types occurring in TAST but allow all byrefs.  Only used on internally-generated types
let CheckTypePermitAllByrefs (cenv: cenv) env m ty = CheckType PermitByRefType.All cenv env m ty

/// Check types occurring in TAST but disallow inner types to be byref or byref-like types.
let CheckTypeNoInnerByrefs cenv env m ty = CheckType PermitByRefType.NoInnerByRefLike cenv env m ty

let CheckTypeInstNoByrefs cenv env m tyargs =
    tyargs |> List.iter (CheckTypeNoByrefs cenv env m)

let CheckTypeInstNoInnerByrefs cenv env m tyargs =
    tyargs |> List.iter (CheckTypeNoInnerByrefs cenv env m)

/// Applied functions get wrapped in coerce nodes for subsumption coercions
let (|OptionalCoerce|) expr =
    match stripDebugPoints expr with
    | Expr.Op (TOp.Coerce, _, [DebugPoints(Expr.App (f, _, _, [], _), _)], _) -> f
    | _ -> expr

/// Check an expression doesn't contain a 'reraise'
let CheckNoReraise cenv freesOpt (body: Expr) =
    if cenv.reportErrors then
        // Avoid recomputing the free variables
        let fvs = match freesOpt with None -> freeInExpr CollectLocals body | Some fvs -> fvs
        if fvs.UsesUnboundRethrow then
            errorR(Error(FSComp.SR.chkErrorContainsCallToRethrow(), body.Range))

/// Examples:
/// I<int> & I<int> => ExactlyEqual.
/// I<int> & I<string> => NotEqual.
/// I<int> & I<'T> => FeasiblyEqual.
/// with '[<Measure>] type kg': I< int<kg> > & I<int> => FeasiblyEqual.
/// with 'type MyInt = int': I<MyInt> & I<int> => FeasiblyEqual.
///
/// The differences could also be nested, example:
/// I<List<int*string>> vs I<List<int*'T>> => FeasiblyEqual.
type TTypeEquality =
    | ExactlyEqual
    | FeasiblyEqual
    | NotEqual

let compareTypesWithRegardToTypeVariablesAndMeasures g amap m ty1 ty2 =

    if (typeEquiv g ty1 ty2) then
        ExactlyEqual
    else
        if (typeEquiv g ty1 ty2 || TypesFeasiblyEquivStripMeasures g amap m ty1 ty2) then
            FeasiblyEqual
        else
            NotEqual

let keyTyByStamp g ty =
    assert isAppTy g ty
    (tcrefOfAppTy g ty).Stamp

let CheckMultipleInterfaceInstantiations cenv (ty:TType) (interfaces:TType list) isObjectExpression m =
    let groups = interfaces |> List.groupBy (keyTyByStamp cenv.g)
    let errors = seq {
        for _, items in groups do
            for i1 in 0 .. items.Length - 1 do
                for i2 in i1 + 1 .. items.Length - 1 do
                    let ty1 = items[i1]
                    let ty2 = items[i2]
                    let tcRef1 = tcrefOfAppTy cenv.g ty1
                    match compareTypesWithRegardToTypeVariablesAndMeasures cenv.g cenv.amap m ty1 ty2 with
                    | ExactlyEqual -> ()
                    | FeasiblyEqual ->
                        match tryLanguageFeatureErrorOption cenv.g.langVersion LanguageFeature.InterfacesWithMultipleGenericInstantiation m with
                        | None -> ()
                        | Some exn -> exn

                        let typ1Str = NicePrint.minimalStringOfType cenv.denv ty1
                        let typ2Str = NicePrint.minimalStringOfType cenv.denv ty2
                        if isObjectExpression then
                            Error(FSComp.SR.typrelInterfaceWithConcreteAndVariableObjectExpression(tcRef1.DisplayNameWithStaticParametersAndUnderscoreTypars, typ1Str, typ2Str),m)
                        else
                            let typStr = NicePrint.minimalStringOfType cenv.denv ty
                            Error(FSComp.SR.typrelInterfaceWithConcreteAndVariable(typStr, tcRef1.DisplayNameWithStaticParametersAndUnderscoreTypars, typ1Str, typ2Str),m)

                    | NotEqual ->
                        match tryLanguageFeatureErrorOption cenv.g.langVersion LanguageFeature.InterfacesWithMultipleGenericInstantiation m with
                        | None -> ()
                        | Some exn -> exn
    }
    match Seq.tryHead errors with
    | None -> ()
    | Some e -> errorR(e)

/// Check an expression, where the expression is in a position where byrefs can be generated
let rec CheckExprNoByrefs cenv env expr =
    CheckExpr cenv env expr PermitByRefExpr.No |> ignore

/// Check a value
and CheckValRef (cenv: cenv) (env: env) v m (ctxt: PermitByRefExpr) =

    if cenv.reportErrors then
        if cenv.g.isSpliceOperator v && not env.quote then errorR(Error(FSComp.SR.chkSplicingOnlyInQuotations(), m))
        if cenv.g.isSpliceOperator v then errorR(Error(FSComp.SR.chkNoFirstClassSplicing(), m))
        if valRefEq cenv.g v cenv.g.addrof_vref  then errorR(Error(FSComp.SR.chkNoFirstClassAddressOf(), m))
        if valRefEq cenv.g v cenv.g.reraise_vref then errorR(Error(FSComp.SR.chkNoFirstClassRethrow(), m))
        if valRefEq cenv.g v cenv.g.nameof_vref then errorR(Error(FSComp.SR.chkNoFirstClassNameOf(), m))
        if cenv.g.langVersion.SupportsFeature LanguageFeature.RefCellNotationInformationals then
            if valRefEq cenv.g v cenv.g.refcell_deref_vref then informationalWarning(Error(FSComp.SR.chkInfoRefcellDeref(), m))
            if valRefEq cenv.g v cenv.g.refcell_assign_vref then informationalWarning(Error(FSComp.SR.chkInfoRefcellAssign(), m))
            if valRefEq cenv.g v cenv.g.refcell_incr_vref then informationalWarning(Error(FSComp.SR.chkInfoRefcellIncr(), m))
            if valRefEq cenv.g v cenv.g.refcell_decr_vref then informationalWarning(Error(FSComp.SR.chkInfoRefcellDecr(), m))

        // ByRefLike-typed values can only occur in permitting ctxts
        if ctxt.Disallow && isByrefLikeTy cenv.g m v.Type then
            errorR(Error(FSComp.SR.chkNoByrefAtThisPoint(v.DisplayName), m))

    if env.isInAppExpr then
        CheckTypePermitAllByrefs cenv env m v.Type // we do checks for byrefs elsewhere
    else
        CheckTypeNoInnerByrefs cenv env m v.Type

/// Check a use of a value
and CheckValUse (cenv: cenv) (env: env) (vref: ValRef, vFlags, m) (ctxt: PermitByRefExpr) =

    let g = cenv.g

    let limit = GetLimitVal cenv env m vref.Deref

    if cenv.reportErrors then

        if vref.IsBaseVal then
            errorR(Error(FSComp.SR.chkLimitationsOfBaseKeyword(), m))

        let isCallOfConstructorOfAbstractType =
            (match vFlags with NormalValUse -> true | _ -> false) &&
            vref.IsConstructor &&
            (match vref.TryDeclaringEntity with Parent tcref -> isAbstractTycon tcref.Deref | _ -> false)

        if isCallOfConstructorOfAbstractType then
            errorR(Error(FSComp.SR.tcAbstractTypeCannotBeInstantiated(), m))

        // This is used to handle this case:
        //     let x = 1
        //     let y = &x
        //     &y
        let isReturnExprBuiltUsingStackReferringByRefLike =
            ctxt.PermitOnlyReturnable &&
            ((HasLimitFlag LimitFlags.ByRef limit && IsLimitEscapingScope env ctxt limit) ||
             HasLimitFlag LimitFlags.StackReferringSpanLike limit)

        if isReturnExprBuiltUsingStackReferringByRefLike then
            let isSpanLike = isSpanLikeTy g m vref.Type
            let isCompGen = vref.IsCompilerGenerated
            match isSpanLike, isCompGen with
            | true, true -> errorR(Error(FSComp.SR.chkNoSpanLikeValueFromExpression(), m))
            | true, false -> errorR(Error(FSComp.SR.chkNoSpanLikeVariable(vref.DisplayName), m))
            | false, true -> errorR(Error(FSComp.SR.chkNoByrefAddressOfValueFromExpression(), m))
            | false, false -> errorR(Error(FSComp.SR.chkNoByrefAddressOfLocal(vref.DisplayName), m))

        let isReturnOfStructThis =
            ctxt.PermitOnlyReturnable &&
            isByrefTy g vref.Type &&
            (vref.IsMemberThisVal)

        if isReturnOfStructThis then
            errorR(Error(FSComp.SR.chkStructsMayNotReturnAddressesOfContents(), m))

    CheckValRef cenv env vref m ctxt

    limit

/// Check an expression, given information about the position of the expression
and CheckForOverAppliedExceptionRaisingPrimitive (cenv: cenv) expr =
    let g = cenv.g
    let expr = stripExpr expr
    let expr = stripDebugPoints expr

    // Some things are more easily checked prior to NormalizeAndAdjustPossibleSubsumptionExprs
    match expr with
    | Expr.App (f, _fty, _tyargs, argsl, _m) ->

        if cenv.reportErrors then

            // Special diagnostics for `raise`, `failwith`, `failwithf`, `nullArg`, `invalidOp` library intrinsics commonly used to raise exceptions
            // to warn on over-application.
            match f with
            | OptionalCoerce(Expr.Val (v, _, funcRange))
                when (valRefEq g v g.raise_vref || valRefEq g v g.failwith_vref || valRefEq g v g.null_arg_vref || valRefEq g v g.invalid_op_vref) ->
                match argsl with
                | [] | [_] -> ()
                | _ :: _ :: _ ->
                    warning(Error(FSComp.SR.checkRaiseFamilyFunctionArgumentCount(v.DisplayName, 1, argsl.Length), funcRange))

            | OptionalCoerce(Expr.Val (v, _, funcRange)) when valRefEq g v g.invalid_arg_vref ->
                match argsl with
                | [] | [_] | [_; _] -> ()
                | _ :: _ :: _ :: _ ->
                    warning(Error(FSComp.SR.checkRaiseFamilyFunctionArgumentCount(v.DisplayName, 2, argsl.Length), funcRange))

            | OptionalCoerce(Expr.Val (failwithfFunc, _, funcRange)) when valRefEq g failwithfFunc g.failwithf_vref  ->
                match argsl with
                | Expr.App (Expr.Val (newFormat, _, _), _, [_; typB; typC; _; _], [Expr.Const (Const.String formatString, formatRange, _)], _) :: xs when valRefEq g newFormat g.new_format_vref ->
                    match CheckFormatStrings.TryCountFormatStringArguments formatRange g false formatString typB typC with
                    | Some n ->
                        let expected = n + 1
                        let actual = List.length xs + 1
                        if expected < actual then
                            warning(Error(FSComp.SR.checkRaiseFamilyFunctionArgumentCount(failwithfFunc.DisplayName, expected, actual), funcRange))
                    | None -> ()
                | _ -> ()
            | _ -> ()
        | _ -> ()

and CheckCallLimitArgs cenv env m returnTy limitArgs (ctxt: PermitByRefExpr) =
    let isReturnByref = isByrefTy cenv.g returnTy
    let isReturnSpanLike = isSpanLikeTy cenv.g m returnTy

    // If return is a byref, and being used as a return, then a single argument cannot be a local-byref or a stack referring span-like.
    let isReturnLimitedByRef =
        isReturnByref &&
        (HasLimitFlag LimitFlags.ByRef limitArgs ||
         HasLimitFlag LimitFlags.StackReferringSpanLike limitArgs)

    // If return is a byref, and being used as a return, then a single argument cannot be a stack referring span-like or a local-byref of a stack referring span-like.
    let isReturnLimitedSpanLike =
        isReturnSpanLike &&
        (HasLimitFlag LimitFlags.StackReferringSpanLike limitArgs ||
         HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limitArgs)

    if cenv.reportErrors then
        if ctxt.PermitOnlyReturnable && ((isReturnLimitedByRef && IsLimitEscapingScope env ctxt limitArgs) || isReturnLimitedSpanLike) then
            if isReturnLimitedSpanLike then
                errorR(Error(FSComp.SR.chkNoSpanLikeValueFromExpression(), m))
            else
                errorR(Error(FSComp.SR.chkNoByrefAddressOfValueFromExpression(), m))

        // You cannot call a function that takes a byref of a span-like (not stack referring) and
        //     either a stack referring span-like or a local-byref of a stack referring span-like.
        let isCallLimited =
            HasLimitFlag LimitFlags.ByRefOfSpanLike limitArgs &&
            (HasLimitFlag LimitFlags.StackReferringSpanLike limitArgs ||
             HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limitArgs)

        if isCallLimited then
            errorR(Error(FSComp.SR.chkNoByrefLikeFunctionCall(), m))

    if isReturnLimitedByRef then
        if isSpanLikeTy cenv.g m (destByrefTy cenv.g returnTy) then
            let isStackReferring =
                HasLimitFlag LimitFlags.StackReferringSpanLike limitArgs ||
                HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limitArgs
            if isStackReferring then
                { limitArgs with flags = LimitFlags.ByRefOfStackReferringSpanLike }
            else
                { limitArgs with flags = LimitFlags.ByRefOfSpanLike }
        else
            { limitArgs with flags = LimitFlags.ByRef }

    elif isReturnLimitedSpanLike then
        { scope = 1; flags = LimitFlags.StackReferringSpanLike }

    elif isReturnByref then
        if isSpanLikeTy cenv.g m (destByrefTy cenv.g returnTy) then
            { limitArgs with flags = LimitFlags.ByRefOfSpanLike }
        else
            { limitArgs with flags = LimitFlags.ByRef }

    elif isReturnSpanLike then
        { scope = 1; flags = LimitFlags.SpanLike }

    else
        { scope = 1; flags = LimitFlags.None }

/// Check call arguments, including the return argument.
and CheckCall cenv env m returnTy args ctxts ctxt =
    let limitArgs = CheckExprs cenv env args ctxts
    CheckCallLimitArgs cenv env m returnTy limitArgs ctxt

/// Check call arguments, including the return argument. The receiver argument is handled differently.
and CheckCallWithReceiver cenv env m returnTy args ctxts ctxt =
    match args with
    | [] -> failwith "CheckCallWithReceiver: Argument list is empty."
    | receiverArg :: args ->

        let receiverContext, ctxts =
            match ctxts with
            | [] -> PermitByRefExpr.No, []
            | ctxt :: ctxts -> ctxt, ctxts

        let receiverLimit = CheckExpr cenv env receiverArg receiverContext
        let limitArgs =
            let limitArgs = CheckExprs cenv env args ctxts
            // We do not include the receiver's limit in the limit args unless the receiver is a stack referring span-like.
            if HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike receiverLimit then
                // Scope is 1 to ensure any by-refs returned can only be prevented for out of scope of the function/method, not visibility.
                CombineTwoLimits limitArgs { receiverLimit with scope = 1 }
            else
                limitArgs
        CheckCallLimitArgs cenv env m returnTy limitArgs ctxt

and CheckExprLinear (cenv: cenv) (env: env) expr (ctxt: PermitByRefExpr) (contf : Limit -> Limit) : Limit =
    match expr with
    | Expr.Sequential (e1, e2, NormalSeq, _) ->
        CheckExprNoByrefs cenv env e1
        // tailcall
        CheckExprLinear cenv env e2 ctxt contf

    | Expr.Let (TBind(v, _bindRhs, _) as bind, body, _, _) ->
        let isByRef = isByrefTy cenv.g v.Type

        let bindingContext =
            if isByRef then
                PermitByRefExpr.YesReturnable
            else
                PermitByRefExpr.Yes

        let limit = CheckBinding cenv { env with returnScope = env.returnScope + 1 } false bindingContext bind
        BindVal cenv env v
        LimitVal cenv v { limit with scope = if isByRef then limit.scope else env.returnScope }
        // tailcall
        CheckExprLinear cenv env body ctxt contf

    | LinearOpExpr (_op, tyargs, argsHead, argLast, m) ->
        CheckTypeInstNoByrefs cenv env m tyargs
        argsHead |> List.iter (CheckExprNoByrefs cenv env)
        // tailcall
        CheckExprLinear cenv env argLast PermitByRefExpr.No (fun _ -> contf NoLimit)

    | LinearMatchExpr (_spMatch, _exprm, dtree, tg1, e2, m, ty) ->
        CheckTypeNoInnerByrefs cenv env m ty
        CheckDecisionTree cenv env dtree
        let lim1 = CheckDecisionTreeTarget cenv env ctxt tg1
        // tailcall
        CheckExprLinear cenv env e2 ctxt (fun lim2 -> contf (CombineTwoLimits lim1 lim2))

    | Expr.DebugPoint (_, innerExpr) ->
        CheckExprLinear cenv env innerExpr ctxt contf

    | _ ->
        // not a linear expression
        CheckExpr cenv env expr ctxt
        |> contf

/// Check a resumable code expression (the body of a ResumableCode delegate or
/// the body of the MoveNextMethod for a state machine)
and TryCheckResumableCodeConstructs cenv env expr : bool =
    let g = cenv.g

    match env.resumableCode with
    | Resumable.None ->
        CheckNoResumableStmtConstructs cenv env expr
        false
    | Resumable.ResumableExpr allowed ->
        match expr with
        | IfUseResumableStateMachinesExpr g (thenExpr, elseExpr) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.ResumableExpr true } thenExpr
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } elseExpr
            true

        | ResumableEntryMatchExpr g (noneBranchExpr, someVar, someBranchExpr, _rebuild) ->
            if not allowed then
                errorR(Error(FSComp.SR.tcInvalidResumableConstruct("__resumableEntry"), expr.Range))
            CheckExprNoByrefs cenv env noneBranchExpr
            BindVal cenv env someVar
            CheckExprNoByrefs cenv env someBranchExpr
            true

        | ResumeAtExpr g pcExpr  ->
            if not allowed then
                errorR(Error(FSComp.SR.tcInvalidResumableConstruct("__resumeAt"), expr.Range))
            CheckExprNoByrefs cenv env pcExpr
            true

        | ResumableCodeInvoke g (_, f, args, _, _) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } f
            for arg in args do
                CheckExprPermitByRefLike cenv { env with resumableCode = Resumable.None } arg |> ignore
            true

        | SequentialResumableCode g (e1, e2, _m, _recreate) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.ResumableExpr allowed }e1
            CheckExprNoByrefs cenv env e2
            true

        | WhileExpr (_sp1, _sp2, guardExpr, bodyExpr, _m) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } guardExpr
            CheckExprNoByrefs cenv env bodyExpr
            true

        // Integer for-loops are allowed but their bodies are not currently resumable
        | IntegerForLoopExpr (_sp1, _sp2, _style, e1, e2, v, e3, _m) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } e1
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } e2
            BindVal cenv env v
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } e3
            true

        | TryWithExpr (_spTry, _spWith, _resTy, bodyExpr, _filterVar, filterExpr, _handlerVar, handlerExpr, _m) ->
            CheckExprNoByrefs cenv env bodyExpr
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } handlerExpr
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } filterExpr
            true

        | TryFinallyExpr (_sp1, _sp2, _ty, e1, e2, _m) ->
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } e1
            CheckExprNoByrefs cenv { env with resumableCode = Resumable.None } e2
            true

        | Expr.Match (_spBind, _exprm, dtree, targets, _m, _ty) ->
            targets |> Array.iter(fun (TTarget(vs, targetExpr, _)) ->
                BindVals cenv env vs
                CheckExprNoByrefs cenv env targetExpr)
            CheckDecisionTree cenv { env with resumableCode = Resumable.None } dtree
            true

        | Expr.Let (bind, bodyExpr, _m, _)
                // Restriction: resumable code can't contain local constrained generic functions
                when  bind.Var.IsCompiledAsTopLevel || not (IsGenericValWithGenericConstraints g bind.Var) ->
            CheckBinding cenv { env with resumableCode = Resumable.None } false PermitByRefExpr.Yes bind |> ignore<Limit>
            BindVal cenv env bind.Var
            CheckExprNoByrefs cenv env bodyExpr
            true

        // LetRec bindings may not appear as part of resumable code (more careful work is needed to make them compilable)
        | Expr.LetRec(_bindings, bodyExpr, _range, _frees) when allowed ->
            errorR(Error(FSComp.SR.tcResumableCodeContainsLetRec(), expr.Range))
            CheckExprNoByrefs cenv env bodyExpr
            true

        // This construct arises from the 'mkDefault' in the 'Throw' case of an incomplete pattern match
        | Expr.Const (Const.Zero, _, _) ->
            true

        | Expr.DebugPoint (_, innerExpr) ->
            TryCheckResumableCodeConstructs cenv env innerExpr

        | _ ->
            false

/// Check an expression, given information about the position of the expression
and CheckExpr (cenv: cenv) (env: env) origExpr (ctxt: PermitByRefExpr) : Limit =

    // Guard the stack for deeply nested expressions
    cenv.stackGuard.Guard <| fun () ->

    let g = cenv.g

    let origExpr = stripExpr origExpr

    // CheckForOverAppliedExceptionRaisingPrimitive is more easily checked prior to NormalizeAndAdjustPossibleSubsumptionExprs
    CheckForOverAppliedExceptionRaisingPrimitive cenv origExpr
    let expr = NormalizeAndAdjustPossibleSubsumptionExprs g origExpr
    let expr = stripExpr expr

    match TryCheckResumableCodeConstructs cenv env expr with
    | true ->
        // we've handled the special cases of resumable code and don't do other checks.
        NoLimit
    | false ->

    // Handle ResumableExpr --> other expression
    let env = { env with resumableCode = Resumable.None }

    match expr with
    | LinearOpExpr _
    | LinearMatchExpr _
    | Expr.Let _
    | Expr.Sequential (_, _, NormalSeq, _)
    | Expr.DebugPoint _ ->
        CheckExprLinear cenv env expr ctxt id

    | Expr.Sequential (e1, e2, ThenDoSeq, _) ->
        CheckExprNoByrefs cenv env e1
        CheckExprNoByrefs cenv env e2
        NoLimit

    | Expr.Const (_, m, ty) ->
        CheckTypeNoInnerByrefs cenv env m ty
        NoLimit

    | Expr.Val (vref, vFlags, m) ->
        CheckValUse cenv env (vref, vFlags, m) ctxt

    | Expr.Quote (ast, savedConv, _isFromQueryExpression, m, ty) ->
        CheckQuoteExpr cenv env (ast, savedConv, m, ty)

    | StructStateMachineExpr g info ->
        CheckStructStateMachineExpr cenv env expr info

    | Expr.Obj (_, ty, basev, superInitCall, overrides, iimpls, m) ->
        CheckObjectExpr cenv env (ty, basev, superInitCall, overrides, iimpls, m)

    // Allow base calls to F# methods
    | Expr.App (InnerExprPat(ExprValWithPossibleTypeInst(v, vFlags, _, _)  as f), _fty, tyargs, Expr.Val (baseVal, _, _) :: rest, m)
          when ((match vFlags with VSlotDirectCall -> true | _ -> false) &&
                baseVal.IsBaseVal) ->

        CheckFSharpBaseCall cenv env expr (v, f, _fty, tyargs, baseVal, rest, m)

    // Allow base calls to IL methods
    | Expr.Op (TOp.ILCall (isVirtual, _, _, _, _, _, _, ilMethRef, enclTypeInst, methInst, retTypes), tyargs, Expr.Val (baseVal, _, _) :: rest, m)
          when not isVirtual && baseVal.IsBaseVal ->

        CheckILBaseCall cenv env (ilMethRef, enclTypeInst, methInst, retTypes, tyargs, baseVal, rest, m)

    | Expr.Op (op, tyargs, args, m) ->
        CheckExprOp cenv env (op, tyargs, args, m) ctxt expr

    // Allow 'typeof<System.Void>' calls as a special case, the only accepted use of System.Void!
    | TypeOfExpr g ty when isVoidTy g ty ->
        NoLimit

    // Allow 'typedefof<System.Void>' calls as a special case, the only accepted use of System.Void!
    | TypeDefOfExpr g ty when isVoidTy g ty ->
        NoLimit

    // Allow '%expr' in quotations
    | Expr.App (Expr.Val (vref, _, _), _, tinst, [arg], m) when g.isSpliceOperator vref && env.quote ->
        CheckSpliceApplication cenv env (tinst, arg, m)

    // Check an application
    | Expr.App (f, _fty, tyargs, argsl, m) ->
        CheckApplication cenv env expr (f, tyargs, argsl, m) ctxt

    | Expr.Lambda (_, _, _, argvs, _, m, bodyTy) ->
        CheckLambda cenv env expr (argvs, m, bodyTy)

    | Expr.TyLambda (_, tps, _, m, bodyTy)  ->
        CheckTyLambda cenv env expr (tps, m, bodyTy)

    | Expr.TyChoose (tps, e1, _)  ->
        let env = BindTypars g env tps
        CheckExprNoByrefs cenv env e1
        NoLimit

    | Expr.Match (_, _, dtree, targets, m, ty) ->
        CheckMatch cenv env ctxt (dtree, targets, m, ty)

    | Expr.LetRec (binds, bodyExpr, _, _) ->
        CheckLetRec cenv env (binds, bodyExpr)

    | Expr.StaticOptimization (constraints, e2, e3, m) ->
        CheckStaticOptimization cenv env (constraints, e2, e3, m)

    | Expr.WitnessArg _ ->
        NoLimit

    | Expr.Link _ ->
        failwith "Unexpected reclink"

and CheckQuoteExpr cenv env (ast, savedConv, m, ty) =
    let g = cenv.g
    CheckExprNoByrefs cenv {env with quote=true} ast
    if cenv.reportErrors then
        cenv.usesQuotations <- true

        // Translate the quotation to quotation data
        try
            let doData suppressWitnesses =
                let qscope = QuotationTranslator.QuotationGenerationScope.Create (g, cenv.amap, cenv.viewCcu, cenv.tcVal, QuotationTranslator.IsReflectedDefinition.No)
                let qdata = QuotationTranslator.ConvExprPublic qscope suppressWitnesses ast
                let typeDefs, spliceTypes, spliceExprs = qscope.Close()
                typeDefs, List.map fst spliceTypes, List.map fst spliceExprs, qdata

            let data1 = doData true
            let data2 = doData false
            match savedConv.Value with
            | None ->
                savedConv.Value <- Some (data1, data2)
            | Some _ ->
                ()
        with QuotationTranslator.InvalidQuotedTerm e ->
            errorRecovery e m

    CheckTypeNoByrefs cenv env m ty
    NoLimit

and CheckStructStateMachineExpr cenv env expr info =

    let g = cenv.g
    let (_dataTy,
         (moveNextThisVar, moveNextExpr),
         (setStateMachineThisVar, setStateMachineStateVar, setStateMachineBody),
         (afterCodeThisVar, afterCodeBody)) = info

    if not (g.langVersion.SupportsFeature LanguageFeature.ResumableStateMachines) then
        error(Error(FSComp.SR.tcResumableCodeNotSupported(), expr.Range))

    BindVals cenv env [moveNextThisVar; setStateMachineThisVar; setStateMachineStateVar; afterCodeThisVar]
    CheckExprNoByrefs cenv { env with resumableCode = Resumable.ResumableExpr true } moveNextExpr
    CheckExprNoByrefs cenv env setStateMachineBody
    CheckExprNoByrefs cenv env afterCodeBody
    NoLimit

and CheckObjectExpr cenv env (ty, basev, superInitCall, overrides, iimpls, m) =
    let g = cenv.g
    CheckExprNoByrefs cenv env superInitCall
    CheckMethods cenv env basev (ty, overrides)
    CheckInterfaceImpls cenv env basev iimpls
    CheckTypeNoByrefs cenv env m ty

    let interfaces =
        [ if isInterfaceTy g ty then
            yield! AllSuperTypesOfType g cenv.amap m AllowMultiIntfInstantiations.Yes ty
          for ty, _ in iimpls do
            yield! AllSuperTypesOfType g cenv.amap m AllowMultiIntfInstantiations.Yes ty  ]
        |> List.filter (isInterfaceTy g)

    CheckMultipleInterfaceInstantiations cenv ty interfaces true m
    NoLimit

and CheckFSharpBaseCall cenv env expr (v, f, _fty, tyargs, baseVal, rest, m) =
    let g = cenv.g
    let memberInfo = Option.get v.MemberInfo
    if memberInfo.MemberFlags.IsDispatchSlot then
        errorR(Error(FSComp.SR.tcCannotCallAbstractBaseMember(v.DisplayName), m))
        NoLimit
    else
        let env = { env with isInAppExpr = true }
        let returnTy = tyOfExpr g expr

        CheckValRef cenv env v m PermitByRefExpr.No
        CheckValRef cenv env baseVal m PermitByRefExpr.No
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckTypeNoInnerByrefs cenv env m returnTy
        CheckExprs cenv env rest (mkArgsForAppliedExpr true rest f)

and CheckILBaseCall cenv env (ilMethRef, enclTypeInst, methInst, retTypes, tyargs, baseVal, rest, m) =
    let g = cenv.g
    // Disallow calls to abstract base methods on IL types.
    match tryTcrefOfAppTy g baseVal.Type with
    | ValueSome tcref when tcref.IsILTycon ->
        try
            let mdef =
                match tcref.ILTyconInfo with
                | TILObjectReprData(scoref, _, _) ->
                    resolveILMethodRefWithRescope (rescopeILType scoref) tcref.ILTyconRawMetadata ilMethRef

            if mdef.IsAbstract then
                errorR(Error(FSComp.SR.tcCannotCallAbstractBaseMember(mdef.Name), m))
        with _ -> ()
    | _ -> ()

    CheckTypeInstNoByrefs cenv env m tyargs
    CheckTypeInstNoByrefs cenv env m enclTypeInst
    CheckTypeInstNoByrefs cenv env m methInst
    CheckTypeInstNoByrefs cenv env m retTypes
    CheckValRef cenv env baseVal m PermitByRefExpr.No
    CheckExprsPermitByRefLike cenv env rest

and CheckSpliceApplication cenv env (tinst, arg, m) =
    CheckTypeInstNoInnerByrefs cenv env m tinst // it's the splice operator, a byref instantiation is allowed
    CheckExprNoByrefs cenv env arg
    NoLimit

and CheckApplication cenv env expr (f, tyargs, argsl, m) ctxt =
    let g = cenv.g
    match expr with
    | ResumableCodeInvoke g _ ->
        warning(Error(FSComp.SR.tcResumableCodeInvocation(), m))
    | _ -> ()

    let returnTy = tyOfExpr g expr

    // This is to handle recursive cases. Don't check 'returnTy' again if we are still inside a app expression.
    if not env.isInAppExpr then
        CheckTypeNoInnerByrefs cenv env m returnTy

    let env = { env with isInAppExpr = true }

    CheckTypeInstNoByrefs cenv env m tyargs
    CheckExprNoByrefs cenv env f

    let hasReceiver =
        match f with
        | Expr.Val (vref, _, _) when vref.IsInstanceMember && not argsl.IsEmpty -> true
        | _ -> false

    let ctxts = mkArgsForAppliedExpr false argsl f
    if hasReceiver then
        CheckCallWithReceiver cenv env m returnTy argsl ctxts ctxt
    else
        CheckCall cenv env m returnTy argsl ctxts ctxt

and CheckLambda cenv env expr (argvs, m, bodyTy) =
    let valReprInfo = ValReprInfo ([], [argvs |> List.map (fun _ -> ValReprInfo.unnamedTopArg1)], ValReprInfo.unnamedRetVal)
    let ty = mkMultiLambdaTy cenv.g m argvs bodyTy in
    CheckLambdas false None cenv env false valReprInfo false expr m ty PermitByRefExpr.Yes

and CheckTyLambda cenv env expr (tps, m, bodyTy) =
    let valReprInfo = ValReprInfo (ValReprInfo.InferTyparInfo tps, [], ValReprInfo.unnamedRetVal)
    let ty = mkForallTyIfNeeded tps bodyTy in
    CheckLambdas false None cenv env false valReprInfo false expr m ty PermitByRefExpr.Yes

and CheckMatch cenv env ctxt (dtree, targets, m, ty) =
    CheckTypeNoInnerByrefs cenv env m ty // computed byrefs allowed at each branch
    CheckDecisionTree cenv env dtree
    CheckDecisionTreeTargets cenv env targets ctxt

and CheckLetRec cenv env (binds, bodyExpr) =
    BindVals cenv env (valsOfBinds binds)
    CheckBindings cenv env binds
    CheckExprNoByrefs cenv env bodyExpr
    NoLimit

and CheckStaticOptimization cenv env (constraints, e2, e3, m) =
    CheckExprNoByrefs cenv env e2
    CheckExprNoByrefs cenv env e3
    constraints |> List.iter (function
        | TTyconEqualsTycon(ty1, ty2) ->
            CheckTypeNoByrefs cenv env m ty1
            CheckTypeNoByrefs cenv env m ty2
        | TTyconIsStruct ty1 ->
            CheckTypeNoByrefs cenv env m ty1)
    NoLimit

and CheckMethods cenv env baseValOpt (ty, methods) =
    methods |> List.iter (CheckMethod cenv env baseValOpt ty)

and CheckMethod cenv env baseValOpt ty (TObjExprMethod(_, attribs, tps, vs, body, m)) =
    let env = BindTypars cenv.g env tps
    let vs = List.concat vs
    let env = BindArgVals env vs
    let env =
        // Body of ResumableCode delegate
        if isResumableCodeTy cenv.g ty then
           if not (cenv.g.langVersion.SupportsFeature LanguageFeature.ResumableStateMachines) then
               error(Error(FSComp.SR.tcResumableCodeNotSupported(), m))
           { env with resumableCode = Resumable.ResumableExpr false }
        else
           { env with resumableCode = Resumable.None }
    CheckAttribs cenv env attribs
    CheckNoReraise cenv None body
    CheckEscapes cenv true m (match baseValOpt with Some x -> x :: vs | None -> vs) body |> ignore
    CheckExpr cenv { env with returnScope = env.returnScope + 1 } body PermitByRefExpr.YesReturnableNonLocal |> ignore

and CheckInterfaceImpls cenv env baseValOpt l =
    l |> List.iter (CheckInterfaceImpl cenv env baseValOpt)

and CheckInterfaceImpl cenv env baseValOpt overrides =
    CheckMethods cenv env baseValOpt overrides

and CheckNoResumableStmtConstructs cenv _env expr =
    let g = cenv.g
    match expr with
    | Expr.Val (v, _, m)
        when valRefEq g v g.cgh__resumeAt_vref ||
             valRefEq g v g.cgh__resumableEntry_vref ||
             valRefEq g v g.cgh__stateMachine_vref ->
        errorR(Error(FSComp.SR.tcInvalidResumableConstruct(v.DisplayName), m))
    | _ -> ()

and CheckExprOp cenv env (op, tyargs, args, m) ctxt expr =
    let g = cenv.g

    // Ensure anonymous record type requirements are recorded
    match op with
    | TOp.AnonRecdGet (anonInfo, _)
    | TOp.AnonRecd anonInfo ->
        RecordAnonRecdInfo cenv anonInfo
    | _ -> ()

    // Special cases
    match op, tyargs, args with
    // Handle these as special cases since mutables are allowed inside their bodies
    | TOp.While _, _, [Expr.Lambda (_, _, _, [_], e1, _, _);Expr.Lambda (_, _, _, [_], e2, _, _)]  ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprsNoByRefLike cenv env [e1;e2]

    | TOp.TryFinally _, [_], [Expr.Lambda (_, _, _, [_], e1, _, _); Expr.Lambda (_, _, _, [_], e2, _, _)] ->
        CheckTypeInstNoInnerByrefs cenv env m tyargs  // result of a try/finally can be a byref
        let limit = CheckExpr cenv env e1 ctxt   // result of a try/finally can be a byref if in a position where the overall expression is can be a byref
        CheckExprNoByrefs cenv env e2
        limit

    | TOp.IntegerForLoop _, _, [Expr.Lambda (_, _, _, [_], e1, _, _);Expr.Lambda (_, _, _, [_], e2, _, _);Expr.Lambda (_, _, _, [_], e3, _, _)]  ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprsNoByRefLike cenv env [e1;e2;e3]

    | TOp.TryWith _, [_], [Expr.Lambda (_, _, _, [_], e1, _, _); Expr.Lambda (_, _, _, [_], _e2, _, _); Expr.Lambda (_, _, _, [_], e3, _, _)] ->
        CheckTypeInstNoInnerByrefs cenv env m tyargs  // result of a try/catch can be a byref
        let limit1 = CheckExpr cenv env e1 ctxt // result of a try/catch can be a byref if in a position where the overall expression is can be a byref
        // [(* e2; -- don't check filter body - duplicates logic in 'catch' body *) e3]
        let limit2 = CheckExpr cenv env e3 ctxt // result of a try/catch can be a byref if in a position where the overall expression is can be a byref
        CombineTwoLimits limit1 limit2

    | TOp.ILCall (_, _, _, _, _, _, _, ilMethRef, enclTypeInst, methInst, retTypes), _, _ ->

        CheckTypeInstNoByrefs cenv env m tyargs

        match enclTypeInst,methInst with
        | [],[] -> ()
        | enclTypeInst,methInst ->
            let tyconRef = ImportILTypeRef cenv.amap m ilMethRef.DeclaringTypeRef
            match tyconRef.TypeReprInfo with
            | TILObjectRepr(TILObjectReprData(scoref, _, tdef)) ->
                (enclTypeInst,tdef.GenericParams)
                ||> List.iter2 (fun typeInst typeGeneric ->
                    if not typeGeneric.HasAllowsRefStruct then
                        CheckTypeNoByrefs cenv env m typeInst)

                match methInst with
                | [] -> ()
                | methInst ->
                    let methDef = resolveILMethodRefWithRescope (rescopeILType scoref) tdef ilMethRef
                    (methInst,methDef.GenericParams)
                    ||> List.iter2 (fun methInst methGeneric ->
                        if not methGeneric.HasAllowsRefStruct then
                            CheckTypeNoByrefs cenv env m methInst)

            | _ -> ()

        CheckTypeInstNoInnerByrefs cenv env m retTypes // permit byref returns

        let hasReceiver =
            (ilMethRef.CallingConv.IsInstance || ilMethRef.CallingConv.IsInstanceExplicit) &&
            not args.IsEmpty

        let returnTy = tyOfExpr g expr

        let argContexts = List.init args.Length (fun _ -> PermitByRefExpr.Yes)

        match retTypes with
        | [ty] when ctxt.PermitOnlyReturnable && isByrefLikeTy g m ty ->
            if hasReceiver then
                CheckCallWithReceiver cenv env m returnTy args argContexts ctxt
            else
                CheckCall cenv env m returnTy args argContexts ctxt
        | _ ->
            if hasReceiver then
                CheckCallWithReceiver cenv env m returnTy args argContexts PermitByRefExpr.Yes
            else
                CheckCall cenv env m returnTy args argContexts PermitByRefExpr.Yes

    | TOp.Tuple tupInfo, _, _ when not (evalTupInfoIsStruct tupInfo) ->
        match ctxt with
        | PermitByRefExpr.YesTupleOfArgs nArity ->
            if cenv.reportErrors then
                if args.Length <> nArity then
                    errorR(InternalError("Tuple arity does not correspond to planned function argument arity", m))
            // This tuple should not be generated. The known function arity
            // means it just bundles arguments.
            CheckExprsPermitByRefLike cenv env args
        | _ ->
            CheckTypeInstNoByrefs cenv env m tyargs
            CheckExprsNoByRefLike cenv env args

    | TOp.LValueOp (LAddrOf _, vref), _, _ ->
        let limit1 = GetLimitValByRef cenv env m vref.Deref
        let limit2 = CheckExprsNoByRefLike cenv env args
        let limit = CombineTwoLimits limit1 limit2

        if cenv.reportErrors  then

            if ctxt.Disallow then
                errorR(Error(FSComp.SR.chkNoAddressOfAtThisPoint(vref.DisplayName), m))

            let returningAddrOfLocal =
                ctxt.PermitOnlyReturnable &&
                HasLimitFlag LimitFlags.ByRef limit &&
                IsLimitEscapingScope env ctxt limit

            if returningAddrOfLocal then
                if vref.IsCompilerGenerated then
                    errorR(Error(FSComp.SR.chkNoByrefAddressOfValueFromExpression(), m))
                else
                    errorR(Error(FSComp.SR.chkNoByrefAddressOfLocal(vref.DisplayName), m))

        limit

    | TOp.LValueOp (LByrefSet, vref), _, [arg] ->
        let limit = GetLimitVal cenv env m vref.Deref
        let isVrefLimited = not (HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limit)
        let isArgLimited = HasLimitFlag LimitFlags.StackReferringSpanLike (CheckExprPermitByRefLike cenv env arg)
        if isVrefLimited && isArgLimited then
            errorR(Error(FSComp.SR.chkNoWriteToLimitedSpan(vref.DisplayName), m))
        NoLimit

    | TOp.LValueOp (LByrefGet, vref), _, [] ->
        let limit = GetLimitVal cenv env m vref.Deref
        if HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limit then

            if cenv.reportErrors && ctxt.PermitOnlyReturnable then
                if vref.IsCompilerGenerated then
                    errorR(Error(FSComp.SR.chkNoSpanLikeValueFromExpression(), m))
                else
                    errorR(Error(FSComp.SR.chkNoSpanLikeVariable(vref.DisplayName), m))

            { scope = 1; flags = LimitFlags.StackReferringSpanLike }
        elif HasLimitFlag LimitFlags.ByRefOfSpanLike limit then
            { scope = 1; flags = LimitFlags.SpanLike }
        else
            { scope = 1; flags = LimitFlags.None }

    | TOp.LValueOp (LSet, vref), _, [arg] ->
        let isVrefLimited = not (HasLimitFlag LimitFlags.StackReferringSpanLike (GetLimitVal cenv env m vref.Deref))
        let isArgLimited = HasLimitFlag LimitFlags.StackReferringSpanLike (CheckExprPermitByRefLike cenv env arg)
        if isVrefLimited && isArgLimited then
            errorR(Error(FSComp.SR.chkNoWriteToLimitedSpan(vref.DisplayName), m))
        NoLimit

    | TOp.AnonRecdGet _, _, [arg1]
    | TOp.TupleFieldGet _, _, [arg1] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprsPermitByRefLike cenv env [arg1]

    | TOp.ValFieldGet _rf, _, [arg1] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        //See mkRecdFieldGetViaExprAddr -- byref arg1 when #args =1
        // Property getters on mutable structs come through here.
        CheckExprsPermitByRefLike cenv env [arg1]

    | TOp.ValFieldSet rf, _, [arg1;arg2] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        // See mkRecdFieldSetViaExprAddr -- byref arg1 when #args=2
        // Field setters on mutable structs come through here
        let limit1 = CheckExprPermitByRefLike cenv env arg1
        let limit2 = CheckExprPermitByRefLike cenv env arg2

        let isLhsLimited = not (HasLimitFlag LimitFlags.ByRefOfStackReferringSpanLike limit1)
        let isRhsLimited = HasLimitFlag LimitFlags.StackReferringSpanLike limit2
        if isLhsLimited && isRhsLimited then
            errorR(Error(FSComp.SR.chkNoWriteToLimitedSpan(rf.FieldName), m))
        NoLimit

    | TOp.Coerce, [tgtTy;srcTy], [x] ->
        if TypeDefinitelySubsumesTypeNoCoercion 0 g cenv.amap m tgtTy srcTy then
            CheckExpr cenv env x ctxt
        else
            CheckTypeInstNoByrefs cenv env m tyargs
            CheckExprNoByrefs cenv env x
            NoLimit

    | TOp.Reraise, [_ty1], [] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        NoLimit

    // Check get of static field
    | TOp.ValFieldGetAddr (rfref, _readonly), tyargs, [] ->

        if ctxt.Disallow && cenv.reportErrors && isByrefLikeTy g m (tyOfExpr g expr) then
            errorR(Error(FSComp.SR.chkNoAddressStaticFieldAtThisPoint(rfref.FieldName), m))

        CheckTypeInstNoByrefs cenv env m tyargs
        NoLimit

    // Check get of instance field
    | TOp.ValFieldGetAddr (rfref, _readonly), tyargs, [obj] ->

        if ctxt.Disallow && cenv.reportErrors  && isByrefLikeTy g m (tyOfExpr g expr) then
            errorR(Error(FSComp.SR.chkNoAddressFieldAtThisPoint(rfref.FieldName), m))

        // C# applies a rule where the APIs to struct types can't return the addresses of fields in that struct.
        // There seems no particular reason for this given that other protections in the language, though allowing
        // it would mean "readonly" on a struct doesn't imply immutability-of-contents - it only implies
        if ctxt.PermitOnlyReturnable && (match stripDebugPoints obj with Expr.Val (vref, _, _) -> vref.IsMemberThisVal | _ -> false) && isByrefTy g (tyOfExpr g obj) then
            errorR(Error(FSComp.SR.chkStructsMayNotReturnAddressesOfContents(), m))

        if ctxt.Disallow && cenv.reportErrors  && isByrefLikeTy g m (tyOfExpr g expr) then
            errorR(Error(FSComp.SR.chkNoAddressFieldAtThisPoint(rfref.FieldName), m))

        // This construct is used for &(rx.rfield) and &(rx->rfield). Relax to permit byref types for rx. [See Bug 1263].
        CheckTypeInstNoByrefs cenv env m tyargs

        // Recursively check in same ctxt, e.g. if at PermitOnlyReturnable the obj arg must also be returnable
        CheckExpr cenv env obj ctxt

    | TOp.UnionCaseFieldGet _, _, [arg1] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprPermitByRefLike cenv env arg1

    | TOp.UnionCaseTagGet _, _, [arg1] ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprPermitByRefLike cenv env arg1  // allow byref - it may be address-of-struct

    | TOp.UnionCaseFieldGetAddr (uref, _idx, _readonly), tyargs, [obj] ->

        if ctxt.Disallow && cenv.reportErrors  && isByrefLikeTy g m (tyOfExpr g expr) then
          errorR(Error(FSComp.SR.chkNoAddressFieldAtThisPoint(uref.CaseName), m))

        if ctxt.PermitOnlyReturnable && (match stripDebugPoints obj with Expr.Val (vref, _, _) -> vref.IsMemberThisVal | _ -> false) && isByrefTy g (tyOfExpr g obj) then
            errorR(Error(FSComp.SR.chkStructsMayNotReturnAddressesOfContents(), m))

        CheckTypeInstNoByrefs cenv env m tyargs

        // Recursively check in same ctxt, e.g. if at PermitOnlyReturnable the obj arg must also be returnable
        CheckExpr cenv env obj ctxt

    | TOp.ILAsm (instrs, retTypes), _, _  ->
        CheckTypeInstNoInnerByrefs cenv env m retTypes
        CheckTypeInstNoByrefs cenv env m tyargs
        match instrs, args with
        // Write a .NET instance field
        | [ I_stfld (_alignment, _vol, _fspec) ], _ ->
            // permit byref for lhs lvalue
            // permit byref for rhs lvalue (field would have to have ByRefLike type, i.e. be a field in another ByRefLike type)
            CheckExprsPermitByRefLike cenv env args

        // Read a .NET instance field
        | [ I_ldfld (_alignment, _vol, _fspec) ], _ ->
            // permit byref for lhs lvalue
            CheckExprsPermitByRefLike cenv env args

        // Read a .NET instance field
        | [ I_ldfld (_alignment, _vol, _fspec); AI_nop ], _ ->
            // permit byref for lhs lvalue of readonly value
            CheckExprsPermitByRefLike cenv env args

        | [ I_ldsflda fspec ], [] ->
            if ctxt.Disallow && cenv.reportErrors  && isByrefLikeTy g m (tyOfExpr g expr) then
                errorR(Error(FSComp.SR.chkNoAddressFieldAtThisPoint(fspec.Name), m))

            NoLimit

        | [ I_ldflda fspec ], [obj] ->
            if ctxt.Disallow && cenv.reportErrors  && isByrefLikeTy g m (tyOfExpr g expr) then
                errorR(Error(FSComp.SR.chkNoAddressFieldAtThisPoint(fspec.Name), m))

            // Recursively check in same ctxt, e.g. if at PermitOnlyReturnable the obj arg must also be returnable
            CheckExpr cenv env obj ctxt

        | [ I_ldelema (_, isNativePtr, _, _) ], lhsArray :: indices ->
            if ctxt.Disallow && cenv.reportErrors && not isNativePtr && isByrefLikeTy g m (tyOfExpr g expr) then
                errorR(Error(FSComp.SR.chkNoAddressOfArrayElementAtThisPoint(), m))
            // permit byref for lhs lvalue
            let limit = CheckExprPermitByRefLike cenv env lhsArray
            CheckExprsNoByRefLike cenv env indices |> ignore
            limit

        | [ AI_conv _ ], _ ->
            // permit byref for args to conv
            CheckExprsPermitByRefLike cenv env args

        | _ ->
            CheckExprsNoByRefLike cenv env args

    | TOp.TraitCall _, _, _ ->
        CheckTypeInstNoByrefs cenv env m tyargs
        // allow args to be byref here
        CheckExprsPermitByRefLike cenv env args

    | TOp.Recd _, _, _ ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprsPermitByRefLike cenv env args

    | _ ->
        CheckTypeInstNoByrefs cenv env m tyargs
        CheckExprsNoByRefLike cenv env args

and CheckLambdas isTop (memberVal: Val option) cenv env inlined valReprInfo alwaysCheckNoReraise expr mOrig ety ctxt =
    let g = cenv.g
    let memInfo = memberVal |> Option.bind (fun v -> v.MemberInfo)

    // The valReprInfo here says we are _guaranteeing_ to compile a function value
    // as a .NET method with precisely the corresponding argument counts.
    match stripDebugPoints expr with
    | Expr.TyChoose (tps, e1, m)  ->
        let env = BindTypars g env tps
        CheckLambdas isTop memberVal cenv env inlined valReprInfo alwaysCheckNoReraise e1 m ety ctxt

    | Expr.Lambda (_, _, _, _, _, m, _)
    | Expr.TyLambda (_, _, _, m, _) ->
        let tps, ctorThisValOpt, baseValOpt, vsl, body, bodyTy = destLambdaWithValReprInfo g cenv.amap valReprInfo (expr, ety)
        let env = BindTypars g env tps
        let thisAndBase = Option.toList ctorThisValOpt @ Option.toList baseValOpt
        let restArgs = List.concat vsl
        let syntacticArgs = thisAndBase @ restArgs
        let env = BindArgVals env restArgs

        match memInfo with
        | None -> ()
        | Some mi ->
            // ctorThis and baseVal values are always considered used
            for v in thisAndBase do v.SetHasBeenReferenced()
            // instance method 'this' is always considered used
            match mi.MemberFlags.IsInstance, restArgs with
            | true, firstArg :: _ -> firstArg.SetHasBeenReferenced()
            | _ -> ()
            // any byRef arguments are considered used, as they may be 'out's
            for arg in restArgs do
                if isByrefTy g arg.Type then
                    arg.SetHasBeenReferenced()

        let permitByRefType =
            if isTop then
                PermitByRefType.NoInnerByRefLike
            else
                PermitByRefType.None

        // Check argument types
        for arg in syntacticArgs do
            if arg.InlineIfLambda && (not inlined || not (isFunTy g arg.Type || isFSharpDelegateTy g arg.Type)) then
                errorR(Error(FSComp.SR.tcInlineIfLambdaUsedOnNonInlineFunctionOrMethod(), arg.Range))

            CheckValSpecAux permitByRefType cenv env arg (fun () ->
                if arg.IsCompilerGenerated then
                    errorR(Error(FSComp.SR.chkErrorUseOfByref(), arg.Range))
                else
                    errorR(Error(FSComp.SR.chkInvalidFunctionParameterType(arg.DisplayName, NicePrint.minimalStringOfType cenv.denv arg.Type), arg.Range))
            )

        // Check return type
        CheckTypeAux permitByRefType cenv env mOrig bodyTy (fun () ->
            errorR(Error(FSComp.SR.chkInvalidFunctionReturnType(NicePrint.minimalStringOfType cenv.denv bodyTy), mOrig))
        )

        for arg in syntacticArgs do
            BindVal cenv env arg

        // Check escapes in the body.  Allow access to protected things within members.
        let freesOpt = CheckEscapes cenv memInfo.IsSome m syntacticArgs body

        //  no reraise under lambda expression
        CheckNoReraise cenv freesOpt body

        // Check the body of the lambda
        if isTop && not g.compilingFSharpCore && isByrefLikeTy g m bodyTy then
            // allow byref to occur as return position for byref-typed top level function or method
            CheckExprPermitReturnableByRef cenv env body |> ignore
        else
            CheckExprNoByrefs cenv env body

        // Check byref return types
        if cenv.reportErrors then
            if not isTop then
                CheckForByrefLikeType cenv env m bodyTy (fun () ->
                        errorR(Error(FSComp.SR.chkFirstClassFuncNoByref(), m)))

            elif not g.compilingFSharpCore && isByrefTy g bodyTy then
                // check no byrefs-in-the-byref
                CheckForByrefType cenv env (destByrefTy g bodyTy) (fun () ->
                    errorR(Error(FSComp.SR.chkReturnTypeNoByref(), m)))

            for tp in tps do
                if tp.Constraints |> List.sumBy (function TyparConstraint.CoercesTo(ty, _) when isClassTy g ty -> 1 | _ -> 0) > 1 then
                    errorR(Error(FSComp.SR.chkTyparMultipleClassConstraints(), m))

        NoLimit

    // This path is for expression bindings that are not actually lambdas
    | _ ->
        let m = mOrig
        // Permit byrefs for let x = ...
        CheckTypeNoInnerByrefs cenv env m ety

        let limit =
            if not inlined && (isByrefLikeTy g m ety || isNativePtrTy g ety) then
                // allow byref to occur as RHS of byref binding.
                CheckExpr cenv env expr ctxt
            else
                CheckExprNoByrefs cenv env expr
                NoLimit

        if alwaysCheckNoReraise then
            CheckNoReraise cenv None expr
        limit

and CheckExprs cenv env exprs ctxts : Limit =
    let ctxts = Array.ofList ctxts
    let argArity i = if i < ctxts.Length then ctxts[i] else PermitByRefExpr.No
    exprs
    |> List.mapi (fun i exp -> CheckExpr cenv env exp (argArity i))
    |> CombineLimits

and CheckExprsNoByRefLike cenv env exprs : Limit =
    for expr in exprs do
        CheckExprNoByrefs cenv env expr
    NoLimit

and CheckExprsPermitByRefLike cenv env exprs : Limit =
    exprs
    |> List.map (CheckExprPermitByRefLike cenv env)
    |> CombineLimits

and CheckExprPermitByRefLike cenv env expr : Limit =
    CheckExpr cenv env expr PermitByRefExpr.Yes

and CheckExprPermitReturnableByRef cenv env expr : Limit =
    CheckExpr cenv env expr PermitByRefExpr.YesReturnable

and CheckDecisionTreeTargets cenv env targets ctxt =
    targets
    |> Array.map (CheckDecisionTreeTarget cenv env ctxt)
    |> List.ofArray
    |> CombineLimits

and CheckDecisionTreeTarget cenv env ctxt (TTarget(vs, targetExpr, _)) =
    BindVals cenv env vs
    for v in vs do
        CheckValSpec PermitByRefType.All cenv env v
    CheckExpr cenv env targetExpr ctxt

and CheckDecisionTree cenv env dtree =
    match dtree with
    | TDSuccess (resultExprs, _) ->
        CheckExprsNoByRefLike cenv env resultExprs |> ignore
    | TDBind(bind, rest) ->
        CheckBinding cenv env false PermitByRefExpr.Yes bind |> ignore
        CheckDecisionTree cenv env rest
    | TDSwitch (inpExpr, cases, dflt, m) ->
        CheckDecisionTreeSwitch cenv env (inpExpr, cases, dflt, m)

and CheckDecisionTreeSwitch cenv env (inpExpr, cases, dflt, m) =
    CheckExprPermitByRefLike cenv env inpExpr |> ignore// can be byref for struct union switch
    for (TCase(discrim, dtree)) in cases do
        CheckDecisionTreeTest cenv env m discrim
        CheckDecisionTree cenv env dtree
    dflt |> Option.iter (CheckDecisionTree cenv env)

and CheckDecisionTreeTest cenv env m discrim =
    match discrim with
    | DecisionTreeTest.UnionCase (_, tinst) -> CheckTypeInstNoInnerByrefs cenv env m tinst
    | DecisionTreeTest.ArrayLength (_, ty) -> CheckTypeNoInnerByrefs cenv env m ty
    | DecisionTreeTest.Const _ -> ()
    | DecisionTreeTest.IsNull -> ()
    | DecisionTreeTest.IsInst (srcTy, tgtTy)    -> CheckTypeNoInnerByrefs cenv env m srcTy; CheckTypeNoInnerByrefs cenv env m tgtTy
    | DecisionTreeTest.ActivePatternCase (exp, _, _, _, _, _) -> CheckExprNoByrefs cenv env exp
    | DecisionTreeTest.Error _ -> ()

and CheckAttrib cenv env (Attrib(tcref, _, args, props, _, _, m)) =
    if List.exists (tyconRefEq cenv.g tcref) cenv.g.attribs_Unsupported then
        warning(Error(FSComp.SR.unsupportedAttribute(), m))
    props |> List.iter (fun (AttribNamedArg(_, _, _, expr)) -> CheckAttribExpr cenv env expr)
    args |> List.iter (CheckAttribExpr cenv env)

and CheckAttribExpr cenv env (AttribExpr(expr, vexpr)) =
    CheckExprNoByrefs cenv env expr
    CheckExprNoByrefs cenv env vexpr
    CheckNoReraise cenv None expr
    CheckAttribArgExpr cenv env vexpr

and CheckAttribArgExpr cenv env expr =
    let g = cenv.g
    match expr with

    // Detect standard constants
    | Expr.Const (c, m, _) ->
        match c with
        | Const.Bool _
        | Const.Int32 _
        | Const.SByte  _
        | Const.Int16  _
        | Const.Int32 _
        | Const.Int64 _
        | Const.Byte  _
        | Const.UInt16  _
        | Const.UInt32  _
        | Const.UInt64  _
        | Const.Double _
        | Const.Single _
        | Const.Char _
        | Const.Zero
        | Const.String _
        | Const.Decimal _ -> ()
        | _ ->
            if cenv.reportErrors then
                errorR (Error (FSComp.SR.tastNotAConstantExpression(), m))

    | Expr.Op (TOp.Array, [_elemTy], args, _m) ->
        List.iter (CheckAttribArgExpr cenv env) args
    | TypeOfExpr g _ ->
        ()
    | TypeDefOfExpr g _ ->
        ()
    | Expr.Op (TOp.Coerce, _, [arg], _) ->
        CheckAttribArgExpr cenv env arg
    | EnumExpr g arg1 ->
        CheckAttribArgExpr cenv env arg1
    | AttribBitwiseOrExpr g (arg1, arg2) ->
        CheckAttribArgExpr cenv env arg1
        CheckAttribArgExpr cenv env arg2
    | _ ->
        if cenv.reportErrors then
           errorR (Error (FSComp.SR.chkInvalidCustAttrVal(), expr.Range))

and CheckAttribs cenv env (attribs: Attribs) =
    if isNil attribs then () else
    let tcrefs = [ for Attrib(tcref, _, _, _, gs, _, m) in attribs -> (tcref, gs, m) ]

    // Check for violations of allowMultiple = false
    let duplicates =
        tcrefs
        |> Seq.groupBy (fun (tcref, gs, _) ->
            // Don't allow CompiledNameAttribute on both a property and its getter/setter (see E_CompiledName test)
            if tyconRefEq cenv.g cenv.g.attrib_CompiledNameAttribute.TyconRef tcref then (tcref.Stamp, false) else
            (tcref.Stamp, gs))
        |> Seq.map (fun (_, elems) -> List.last (List.ofSeq elems), Seq.length elems)
        |> Seq.filter (fun (_, count) -> count > 1)
        |> Seq.map fst
        |> Seq.toList
        // Filter for allowMultiple = false
        |> List.filter (fun (tcref, _, m) -> TryFindAttributeUsageAttribute cenv.g m tcref <> Some true)

    if cenv.reportErrors then
       for tcref, _, m in duplicates do
          errorR(Error(FSComp.SR.chkAttrHasAllowMultiFalse(tcref.DisplayName), m))

    attribs |> List.iter (CheckAttrib cenv env)

and CheckValInfo cenv env (ValReprInfo(_, args, ret)) =
    args |> List.iterSquared (CheckArgInfo cenv env)
    ret |> CheckArgInfo cenv env

and CheckArgInfo cenv env (argInfo : ArgReprInfo)  =
    CheckAttribs cenv env argInfo.Attribs

and CheckValSpecAux permitByRefLike cenv env (v: Val) onInnerByrefError =
    v.Attribs |> CheckAttribs cenv env
    v.ValReprInfo |> Option.iter (CheckValInfo cenv env)
    CheckTypeAux permitByRefLike cenv env v.Range v.Type onInnerByrefError

and CheckValSpec permitByRefLike cenv env v =
    CheckValSpecAux permitByRefLike cenv env v (fun () -> errorR(Error(FSComp.SR.chkErrorUseOfByref(), v.Range)))

and AdjustAccess isHidden (cpath: unit -> CompilationPath) access =
    if isHidden then
        let (TAccess l) = access
        // FSharp 1.0 bug 1908: Values hidden by signatures are implicitly at least 'internal'
        let scoref = cpath().ILScopeRef
        let sa = cpath().SyntaxAccess
        TAccess(CompPath(scoref, sa, []) :: l)
    else
        access

and CheckBinding cenv env alwaysCheckNoReraise ctxt (TBind(v, bindRhs, _) as bind) : Limit =
    let vref = mkLocalValRef v
    let g = cenv.g
    let isTop = Option.isSome bind.Var.ValReprInfo
    //printfn "visiting %s..." v.DisplayName

    let env = { env with external = env.external || g.attrib_DllImportAttribute |> Option.exists (fun attr -> HasFSharpAttribute g attr v.Attribs) }

    // Check that active patterns don't have free type variables in their result
    match TryGetActivePatternInfo vref with
    | Some _apinfo when _apinfo.ActiveTags.Length > 1 ->
        if doesActivePatternHaveFreeTypars g vref then
           errorR(Error(FSComp.SR.activePatternChoiceHasFreeTypars(v.LogicalName), v.Range))
    | _ -> ()

    match cenv.potentialUnboundUsesOfVals.TryFind v.Stamp with
    | None -> ()
    | Some m ->
         let nm = v.DisplayName
         errorR(Error(FSComp.SR.chkMemberUsedInInvalidWay(nm, nm, stringOfRange m), v.Range))

    v.Type |> CheckTypePermitAllByrefs cenv env v.Range
    v.Attribs |> CheckAttribs cenv env
    v.ValReprInfo |> Option.iter (CheckValInfo cenv env)

    // Check accessibility
    if (v.IsMemberOrModuleBinding || v.IsMember) && not v.IsIncrClassGeneratedMember then
        let access =  AdjustAccess (IsHiddenVal env.sigToImplRemapInfo v) (fun () -> v.DeclaringEntity.CompilationPath) v.Accessibility
        CheckTypeForAccess cenv env (fun () -> NicePrint.stringOfQualifiedValOrMember cenv.denv cenv.infoReader vref) access v.Range v.Type

    if cenv.reportErrors  then

        // Check top-level let-bound values
        match bind.Var.ValReprInfo with
        | Some info when info.HasNoArgs ->
            CheckForByrefLikeType cenv env v.Range v.Type (fun () -> errorR(Error(FSComp.SR.chkNoByrefAsTopValue(), v.Range)))
        | _ -> ()

        match v.PublicPath with
        | None -> ()
        | _ ->
            if
              // Don't support implicit [<ReflectedDefinition>] on generated members, except the implicit members
              // for 'let' bound functions in classes.
              (not v.IsCompilerGenerated || v.IsIncrClassGeneratedMember) &&

              (// Check the attributes on any enclosing module
               env.reflect ||
               // Check the attributes on the value
               HasFSharpAttribute g g.attrib_ReflectedDefinitionAttribute v.Attribs ||
               // Also check the enclosing type for members - for historical reasons, in the TAST member values
               // are stored in the entity that encloses the type, hence we will not have noticed the ReflectedDefinition
               // on the enclosing type at this point.
               HasFSharpAttribute g g.attrib_ReflectedDefinitionAttribute v.DeclaringEntity.Attribs) then

                if v.IsInstanceMember && v.MemberApparentEntity.IsStructOrEnumTycon then
                    errorR(Error(FSComp.SR.chkNoReflectedDefinitionOnStructMember(), v.Range))
                cenv.usesQuotations <- true

                // If we've already recorded a definition then skip this
                match v.ReflectedDefinition with
                | None -> v.SetValDefn bindRhs
                | Some _ -> ()

                // Run the conversion process over the reflected definition to report any errors in the
                // front end rather than the back end. We currently re-run this during ilxgen.fs but there's
                // no real need for that except that it helps us to bundle all reflected definitions up into
                // one blob for pickling to the binary format
                try
                    let qscope = QuotationTranslator.QuotationGenerationScope.Create (g, cenv.amap, cenv.viewCcu, cenv.tcVal, QuotationTranslator.IsReflectedDefinition.Yes)
                    let methName = v.CompiledName g.CompilerGlobalState
                    QuotationTranslator.ConvReflectedDefinition qscope methName v bindRhs |> ignore

                    let _, _, exprSplices = qscope.Close()
                    if not (isNil exprSplices) then
                        errorR(Error(FSComp.SR.chkReflectedDefCantSplice(), v.Range))
                with
                  | QuotationTranslator.InvalidQuotedTerm e ->
                          errorR e

    match v.MemberInfo with
    | Some memberInfo when not v.IsIncrClassGeneratedMember ->
        match memberInfo.MemberFlags.MemberKind with
        | SynMemberKind.PropertySet | SynMemberKind.PropertyGet  ->
            // These routines raise errors for ill-formed properties
            v |> ReturnTypeOfPropertyVal g |> ignore
            v |> ArgInfosOfPropertyVal g |> ignore

        | _ -> ()

    | _ -> ()

    let valReprInfo  = match bind.Var.ValReprInfo with Some info -> info | _ -> ValReprInfo.emptyValData

    // If the method has ResumableCode argument or return type it must be inline
    // unless warning is suppressed (user must know what they're doing).
    //
    // If the method has ResumableCode return attribute we check the body w.r.t. that
    let env =
        if cenv.reportErrors && isReturnsResumableCodeTy g v.TauType then
            if not (g.langVersion.SupportsFeature LanguageFeature.ResumableStateMachines) then
                error(Error(FSComp.SR.tcResumableCodeNotSupported(), bind.Var.Range))
            if not v.ShouldInline then
                warning(Error(FSComp.SR.tcResumableCodeFunctionMustBeInline(), v.Range))

        if isReturnsResumableCodeTy g v.TauType then
            { env with resumableCode = Resumable.ResumableExpr false }
        else
            env

    CheckLambdas isTop (Some v) cenv env v.ShouldInline valReprInfo alwaysCheckNoReraise bindRhs v.Range v.Type ctxt

and CheckBindings cenv env binds =
    for bind in binds do
        CheckBinding cenv env false PermitByRefExpr.Yes bind |> ignore

// Top binds introduce expression, check they are reraise free.
let CheckModuleBinding cenv env (TBind(v, e, _) as bind) =
    let g = cenv.g
    let isExplicitEntryPoint = HasFSharpAttribute g g.attrib_EntryPointAttribute v.Attribs
    if isExplicitEntryPoint then
        cenv.entryPointGiven <- true
        let isLastCompiland = fst cenv.isLastCompiland
        if not isLastCompiland && cenv.reportErrors  then
            errorR(Error(FSComp.SR.chkEntryPointUsage(), v.Range))

    // Analyze the r.h.s. for the "IsCompiledAsStaticPropertyWithoutField" condition
    if // Mutable values always have fields
       not v.IsMutable &&
       // Literals always have fields
       not (HasFSharpAttribute g g.attrib_LiteralAttribute v.Attribs) &&
       not (HasFSharpAttributeOpt g g.attrib_ThreadStaticAttribute v.Attribs) &&
       not (HasFSharpAttributeOpt g g.attrib_ContextStaticAttribute v.Attribs) &&
       // Having a field makes the binding a static initialization trigger
       IsSimpleSyntacticConstantExpr g e &&
       // Check the thing is actually compiled as a property
       IsCompiledAsStaticProperty g v ||
       (g.compilingFSharpCore && v.Attribs |> List.exists(fun (Attrib(tc, _, _, _, _, _, _)) -> tc.CompiledName = "ValueAsStaticPropertyAttribute"))
     then
        v.SetIsCompiledAsStaticPropertyWithoutField()

    // Check for value name clashes
    begin
        try

          // Skip compiler generated values
          if v.IsCompilerGenerated then () else
          // Skip explicit implementations of interface methods
          if ValIsExplicitImpl g v then () else

          match v.TryDeclaringEntity with
          | ParentNone -> () // this case can happen after error recovery from earlier error
          | Parent _ ->
            let tcref = v.DeclaringEntity
            let hasDefaultAugmentation =
                tcref.IsUnionTycon &&
                match TryFindFSharpAttribute g g.attrib_DefaultAugmentationAttribute tcref.Attribs with
                | Some(Attrib(_, _, [ AttribBoolArg b ], _, _, _, _)) -> b
                | _ -> true (* not hiddenRepr *)

            let kind = (if v.IsMember then "member" else "value")
            let check skipValCheck nm =
                if not skipValCheck &&
                   v.IsModuleBinding &&
                   tcref.ModuleOrNamespaceType.AllValsByLogicalName.ContainsKey nm &&
                   not (valEq tcref.ModuleOrNamespaceType.AllValsByLogicalName[nm] v) then

                    error(Duplicate(kind, v.DisplayName, v.Range))

                if tcref.IsUnionTycon then
                    match nm with
                    | "Tag" -> errorR(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.typeInfoGeneratedProperty(), "Tag", tcref.Range))
                    | "Tags" -> errorR(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.typeInfoGeneratedType(), "Tags", tcref.Range))
                    | _ ->
                        if hasDefaultAugmentation then
                            match tcref.GetUnionCaseByName nm with
                            | Some uc -> error(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.typeInfoUnionCase(), uc.DisplayName, uc.Range))
                            | None -> ()

                            let hasNoArgs =
                                match v.ValReprInfo with
                                | None -> false
                                | Some arity -> List.sum arity.AritiesOfArgs - v.NumObjArgs <= 0 && arity.NumTypars = 0

                            //  In unions user cannot define properties that clash with generated ones
                            if tcref.UnionCasesArray.Length = 1 && hasNoArgs then
                               let ucase1 = tcref.UnionCasesArray[0]
                               for f in ucase1.RecdFieldsArray do
                                   if f.LogicalName = nm then
                                       error(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.typeInfoGeneratedProperty(), f.LogicalName, ucase1.Range))

                // Default augmentation contains the nasty 'Case<UnionCase>' etc.
                let prefix = "New"
                if nm.StartsWithOrdinal prefix then
                    match tcref.GetUnionCaseByName(nm[prefix.Length ..]) with
                    | Some uc -> error(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.chkUnionCaseCompiledForm(), uc.DisplayName, uc.Range))
                    | None -> ()

                // Default augmentation contains the nasty 'Is<UnionCase>' etc.
                let prefix = "Is"
                if not v.IsImplied && nm.StartsWithOrdinal prefix && hasDefaultAugmentation then
                    match tcref.GetUnionCaseByName(nm[prefix.Length ..]) with
                    | Some uc -> error(NameClash(nm, kind, v.DisplayName, v.Range, FSComp.SR.chkUnionCaseDefaultAugmentation(), uc.DisplayName, uc.Range))
                    | None -> ()

                match tcref.GetFieldByName nm with
                | Some rf -> error(NameClash(nm, kind, v.DisplayName, v.Range, "field", rf.LogicalName, rf.Range))
                | None -> ()

            check false v.DisplayNameCoreMangled
            check false v.DisplayName
            check false (v.CompiledName cenv.g.CompilerGlobalState)

            // Check if an F# extension member clashes
            if v.IsExtensionMember then
                tcref.ModuleOrNamespaceType.AllValsAndMembersByLogicalNameUncached[v.LogicalName] |> List.iter (fun v2 ->
                    if v2.IsExtensionMember && not (valEq v v2) && (v.CompiledName cenv.g.CompilerGlobalState) = (v2.CompiledName cenv.g.CompilerGlobalState) then
                        let minfo1 =  FSMeth(g, generalizedTyconRef g tcref, mkLocalValRef v, Some 0UL)
                        let minfo2 =  FSMeth(g, generalizedTyconRef g tcref, mkLocalValRef v2, Some 0UL)
                        if tyconRefEq g v.MemberApparentEntity v2.MemberApparentEntity &&
                           MethInfosEquivByNameAndSig EraseAll true g cenv.amap v.Range minfo1 minfo2 then
                            errorR(Duplicate(kind, v.DisplayName, v.Range)))

            // Properties get 'get_X', only if there are no args
            // Properties get 'get_X'
            match v.ValReprInfo with
            | Some arity when arity.NumCurriedArgs = 0 && arity.NumTypars = 0 -> check false ("get_" + v.DisplayName)
            | _ -> ()
            match v.ValReprInfo with
            | Some arity when v.IsMutable && arity.NumCurriedArgs = 0 && arity.NumTypars = 0 -> check false ("set_" + v.DisplayName)
            | _ -> ()
            match TryChopPropertyName v.DisplayName with
            | Some res -> check true res
            | None -> ()
        with RecoverableException e -> errorRecovery e v.Range
    end

    CheckBinding cenv { env with returnScope = 1 } true PermitByRefExpr.Yes bind |> ignore

//--------------------------------------------------------------------------
// check tycons
//--------------------------------------------------------------------------

let CheckRecdField isUnion cenv env (tycon: Tycon) (rfield: RecdField) =
    let g = cenv.g
    let tcref = mkLocalTyconRef tycon
    let m = rfield.Range
    let fieldTy = stripTyEqns cenv.g rfield.FormalType
    let isHidden =
        IsHiddenTycon env.sigToImplRemapInfo tycon ||
        IsHiddenTyconRepr env.sigToImplRemapInfo tycon ||
        (not isUnion && IsHiddenRecdField env.sigToImplRemapInfo (tcref.MakeNestedRecdFieldRef rfield))
    let access = AdjustAccess isHidden (fun () -> tycon.CompilationPath) rfield.Accessibility
    CheckTypeForAccess cenv env (fun () -> rfield.LogicalName) access m fieldTy

    if isByrefLikeTyconRef g m tcref then
        // Permit Span fields in IsByRefLike types
        CheckTypePermitSpanLike cenv env m fieldTy
        if cenv.reportErrors then
            CheckForByrefType cenv env fieldTy (fun () -> errorR(Error(FSComp.SR.chkCantStoreByrefValue(), tycon.Range)))
    else
        CheckTypeNoByrefs cenv env m fieldTy
        if cenv.reportErrors then
            CheckForByrefLikeType cenv env m fieldTy (fun () -> errorR(Error(FSComp.SR.chkCantStoreByrefValue(), tycon.Range)))

    CheckAttribs cenv env rfield.PropertyAttribs
    CheckAttribs cenv env rfield.FieldAttribs

let CheckEntityDefn cenv env (tycon: Entity) =
#if !NO_TYPEPROVIDERS
    if tycon.IsProvidedGeneratedTycon then () else
#endif
    let g = cenv.g
    let m = tycon.Range
    let tcref = mkLocalTyconRef tycon
    let ty = generalizedTyconRef g tcref

    let env = { env with reflect = env.reflect || HasFSharpAttribute g g.attrib_ReflectedDefinitionAttribute tycon.Attribs }
    let env = BindTypars g env (tycon.Typars m)

    CheckAttribs cenv env tycon.Attribs

    match tycon.TypeAbbrev with
    | Some abbrev -> WarnOnWrongTypeForAccess cenv env (fun () -> tycon.CompiledName) tycon.Accessibility tycon.Range abbrev
    | _ -> ()

    if cenv.reportErrors then

      if not tycon.IsTypeAbbrev then

        let allVirtualMethsInParent =
            match GetSuperTypeOfType g cenv.amap m ty with
            | Some superTy ->
                GetIntrinsicMethInfosOfType cenv.infoReader None AccessibleFromSomewhere AllowMultiIntfInstantiations.Yes IgnoreOverrides m superTy
                |> List.filter (fun minfo -> minfo.IsVirtual)
            | None -> []

        let namesOfMethodsThatMayDifferOnlyInReturnType = ["op_Explicit";"op_Implicit"] (* hardwired *)
        let methodUniquenessIncludesReturnType (minfo: MethInfo) = List.contains minfo.LogicalName namesOfMethodsThatMayDifferOnlyInReturnType
        let MethInfosEquivWrtUniqueness eraseFlag m minfo minfo2 =
            if methodUniquenessIncludesReturnType minfo
            then MethInfosEquivByNameAndSig        eraseFlag true g cenv.amap m minfo minfo2
            else MethInfosEquivByNameAndPartialSig eraseFlag true g cenv.amap m minfo minfo2 (* partial ignores return type *)

        let immediateMeths =
            [ for v in tycon.AllGeneratedInterfaceImplsAndOverrides do yield FSMeth (g, ty, v, None)
              yield! GetImmediateIntrinsicMethInfosOfType (None, AccessibleFromSomewhere) g cenv.amap m ty ]

        let immediateProps = GetImmediateIntrinsicPropInfosOfType (None, AccessibleFromSomewhere) g cenv.amap m ty

        let getHash (hash: Dictionary<string, _>) nm =
            match hash.TryGetValue nm with
            | true, h -> h
            | _ -> []

        // precompute methods grouped by MethInfo.LogicalName
        let hashOfImmediateMeths =
                let h = Dictionary<string, _>()
                for minfo in immediateMeths do
                    match h.TryGetValue minfo.LogicalName with
                    | true, methods ->
                        h[minfo.LogicalName] <- minfo :: methods
                    | false, _ ->
                        h[minfo.LogicalName] <- [minfo]
                h
        let getOtherMethods (minfo : MethInfo) =
            [
                //we have added all methods to the dictionary on the previous step
                let methods = hashOfImmediateMeths[minfo.LogicalName]
                for m in methods do
                    // use referential identity to filter out 'minfo' method
                    if not(Object.ReferenceEquals(m, minfo)) then
                        yield m
            ]

        let hashOfImmediateProps = Dictionary<string, _>()
        for minfo in immediateMeths do
            let nm = minfo.LogicalName
            let m = (match minfo.ArbitraryValRef with None -> m | Some vref -> vref.DefinitionRange)
            let others = getOtherMethods minfo
            // abstract/default pairs of duplicate methods are OK
            let IsAbstractDefaultPair (x: MethInfo) (y: MethInfo) =
                x.IsDispatchSlot && y.IsDefiniteFSharpOverride
            let IsAbstractDefaultPair2 (minfo: MethInfo) (minfo2: MethInfo) =
                IsAbstractDefaultPair minfo minfo2 || IsAbstractDefaultPair minfo2 minfo
            let checkForDup erasureFlag (minfo2: MethInfo) =
                not (IsAbstractDefaultPair2 minfo minfo2)
                && (minfo.IsInstance = minfo2.IsInstance)
                && MethInfosEquivWrtUniqueness erasureFlag m minfo minfo2

            if others |> List.exists (checkForDup EraseAll) then
                if others |> List.exists (checkForDup EraseNone) then
                    errorR(Error(FSComp.SR.chkDuplicateMethod(nm, NicePrint.minimalStringOfType cenv.denv ty), m))
                else
                    errorR(Error(FSComp.SR.chkDuplicateMethodWithSuffix(nm, NicePrint.minimalStringOfType cenv.denv ty), m))

            let numCurriedArgSets = minfo.NumArgs.Length

            if numCurriedArgSets > 1 && others |> List.exists (fun minfo2 -> not (IsAbstractDefaultPair2 minfo minfo2)) then
                errorR(Error(FSComp.SR.chkDuplicateMethodCurried(nm, NicePrint.minimalStringOfType cenv.denv ty), m))

            if numCurriedArgSets > 1 &&
               (minfo.GetParamDatas(cenv.amap, m, minfo.FormalMethodInst)
                |> List.existsSquared (fun (ParamData(isParamArrayArg, _isInArg, isOutArg, optArgInfo, callerInfo, _, reflArgInfo, ty)) ->
                    isParamArrayArg || isOutArg || reflArgInfo.AutoQuote || optArgInfo.IsOptional || callerInfo <> NoCallerInfo || isByrefLikeTy g m ty)) then
                errorR(Error(FSComp.SR.chkCurriedMethodsCantHaveOutParams(), m))

            if numCurriedArgSets = 1 then
                let errorIfNotStringTy m ty callerInfo =
                    if not (typeEquiv g g.string_ty ty) then
                        errorR(Error(FSComp.SR.tcCallerInfoWrongType(callerInfo |> string, "string", NicePrint.minimalStringOfType cenv.denv ty), m))

                let errorIfNotStringOptionTy m ty callerInfo =
                    if not ((isOptionTy g ty) && (typeEquiv g g.string_ty (destOptionTy g ty))) then
                        errorR(Error(FSComp.SR.tcCallerInfoWrongType(callerInfo |> string, "string", NicePrint.minimalStringOfType cenv.denv (destOptionTy g ty)), m))

                minfo.GetParamDatas(cenv.amap, m, minfo.FormalMethodInst)
                |> List.iterSquared (fun (ParamData(_, isInArg, _, optArgInfo, callerInfo, nameOpt, _, ty)) ->
                    ignore isInArg

                    let m =
                        match nameOpt with
                        | Some name -> name.idRange
                        | None -> m

                    match (optArgInfo, callerInfo) with
                    | _, NoCallerInfo -> ()
                    | NotOptional, _ -> errorR(Error(FSComp.SR.tcCallerInfoNotOptional(callerInfo |> string), m))
                    | CallerSide _, CallerLineNumber ->
                        if not (typeEquiv g g.int32_ty ty) then
                            errorR(Error(FSComp.SR.tcCallerInfoWrongType(callerInfo |> string, "int", NicePrint.minimalStringOfType cenv.denv ty), m))
                    | CalleeSide, CallerLineNumber ->
                        if not ((isOptionTy g ty) && (typeEquiv g g.int32_ty (destOptionTy g ty))) then
                            errorR(Error(FSComp.SR.tcCallerInfoWrongType(callerInfo |> string, "int", NicePrint.minimalStringOfType cenv.denv (destOptionTy g ty)), m))
                    | CallerSide _, (CallerFilePath | CallerMemberName) -> errorIfNotStringTy m ty callerInfo
                    | CalleeSide, (CallerFilePath | CallerMemberName) -> errorIfNotStringOptionTy m ty callerInfo
                )

        for pinfo in immediateProps do
            let nm = pinfo.PropertyName
            let m =
                match pinfo.ArbitraryValRef with
                | None -> m
                | Some vref -> vref.DefinitionRange

            if hashOfImmediateMeths.ContainsKey nm then
                errorR(Error(FSComp.SR.chkPropertySameNameMethod(nm, NicePrint.minimalStringOfType cenv.denv ty), m))

            let others = getHash hashOfImmediateProps nm

            if pinfo.HasGetter && pinfo.HasSetter && pinfo.GetterMethod.IsVirtual <> pinfo.SetterMethod.IsVirtual then
                errorR(Error(FSComp.SR.chkGetterSetterDoNotMatchAbstract(nm, NicePrint.minimalStringOfType cenv.denv ty), m))

            let checkForDup erasureFlag pinfo2 =
                  // abstract/default pairs of duplicate properties are OK
                 let IsAbstractDefaultPair (x: PropInfo) (y: PropInfo) =
                     x.IsDispatchSlot && y.IsDefiniteFSharpOverride

                 not (IsAbstractDefaultPair pinfo pinfo2 || IsAbstractDefaultPair pinfo2 pinfo)
                 && PropInfosEquivByNameAndPartialSig erasureFlag g cenv.amap m pinfo pinfo2 (* partial ignores return type *)

            if others |> List.exists (checkForDup EraseAll) then
                if others |> List.exists (checkForDup EraseNone) then
                    errorR(Error(FSComp.SR.chkDuplicateProperty(nm, NicePrint.minimalStringOfType cenv.denv ty), m))
                else
                    errorR(Error(FSComp.SR.chkDuplicatePropertyWithSuffix(nm, NicePrint.minimalStringOfType cenv.denv ty), m))
            // Check to see if one is an indexer and one is not

            if ( (pinfo.HasGetter &&
                  pinfo.HasSetter &&
                  let setterArgs = pinfo.DropGetter().GetParamTypes(cenv.amap, m)
                  let getterArgs = pinfo.DropSetter().GetParamTypes(cenv.amap, m)
                  setterArgs.Length <> getterArgs.Length)
                ||
                 (let nargs = pinfo.GetParamTypes(cenv.amap, m).Length
                  others |> List.exists (fun pinfo2 -> (isNil(pinfo2.GetParamTypes(cenv.amap, m))) <> (nargs = 0)))) then

                  errorR(Error(FSComp.SR.chkPropertySameNameIndexer(nm, NicePrint.minimalStringOfType cenv.denv ty), m))

            // Check to see if the signatures of the both getter and the setter imply the same property type

            if pinfo.HasGetter && pinfo.HasSetter then
                let ty1 = pinfo.DropSetter().GetPropertyType(cenv.amap, m)
                let ty2 = pinfo.DropGetter().GetPropertyType(cenv.amap, m)
                if not (typeEquivAux EraseNone cenv.amap.g ty1 ty2) then
                    if g.langVersion.SupportsFeature(LanguageFeature.WarningIndexedPropertiesGetSetSameType) && pinfo.IsIndexer then
                        warning(Error(FSComp.SR.chkIndexedGetterAndSetterHaveSamePropertyType(pinfo.PropertyName, NicePrint.minimalStringOfType cenv.denv ty1, NicePrint.minimalStringOfType cenv.denv ty2), m))
                    if not pinfo.IsIndexer then
                        errorR(Error(FSComp.SR.chkGetterAndSetterHaveSamePropertyType(pinfo.PropertyName, NicePrint.minimalStringOfType cenv.denv ty1, NicePrint.minimalStringOfType cenv.denv ty2), m))

            hashOfImmediateProps[nm] <- pinfo :: others

        if not (isInterfaceTy g ty) then
            let hashOfAllVirtualMethsInParent = Dictionary<string, _>()
            for minfo in allVirtualMethsInParent do
                let nm = minfo.LogicalName
                let others = getHash hashOfAllVirtualMethsInParent nm
                hashOfAllVirtualMethsInParent[nm] <- minfo :: others
            for minfo in immediateMeths do
                if not minfo.IsDispatchSlot && not minfo.IsVirtual && minfo.IsInstance then
                    let nm = minfo.LogicalName
                    let m = (match minfo.ArbitraryValRef with None -> m | Some vref -> vref.DefinitionRange)
                    let parentMethsOfSameName = getHash hashOfAllVirtualMethsInParent nm
                    let checkForDup erasureFlag (minfo2: MethInfo) = minfo2.IsDispatchSlot && MethInfosEquivByNameAndSig erasureFlag true g cenv.amap m minfo minfo2
                    match parentMethsOfSameName |> List.tryFind (checkForDup EraseAll) with
                    | None -> ()
                    | Some minfo ->
                        let mtext = NicePrint.stringOfMethInfo cenv.infoReader m cenv.denv minfo
                        if parentMethsOfSameName |> List.exists (checkForDup EraseNone) then
                            warning(Error(FSComp.SR.tcNewMemberHidesAbstractMember mtext, m))
                        else
                            warning(Error(FSComp.SR.tcNewMemberHidesAbstractMemberWithSuffix mtext, m))


                if minfo.IsDispatchSlot then
                    let nm = minfo.LogicalName
                    let m = (match minfo.ArbitraryValRef with None -> m | Some vref -> vref.DefinitionRange)
                    let parentMethsOfSameName = getHash hashOfAllVirtualMethsInParent nm
                    let checkForDup erasureFlag minfo2 = MethInfosEquivByNameAndSig erasureFlag true g cenv.amap m minfo minfo2

                    if parentMethsOfSameName |> List.exists (checkForDup EraseAll) then
                        if parentMethsOfSameName |> List.exists (checkForDup EraseNone) then
                            errorR(Error(FSComp.SR.chkDuplicateMethodInheritedType nm, m))
                        else
                            errorR(Error(FSComp.SR.chkDuplicateMethodInheritedTypeWithSuffix nm, m))


    if TyconRefHasAttributeByName m tname_IsByRefLikeAttribute tcref && not tycon.IsStructOrEnumTycon then
        errorR(Error(FSComp.SR.tcByRefLikeNotStruct(), tycon.Range))

    if TyconRefHasAttribute g m g.attrib_IsReadOnlyAttribute tcref && not tycon.IsStructOrEnumTycon then
        errorR(Error(FSComp.SR.tcIsReadOnlyNotStruct(), tycon.Range))

    // Considers TFSharpTyconRepr and TFSharpUnionRepr.
    // [Review] are all cases covered: TILObjectRepr, TAsmRepr. [Yes - these are FSharp.Core.dll only]
    tycon.AllFieldsArray |> Array.iter (CheckRecdField false cenv env tycon)

    // Abstract slots can have byref arguments and returns
    for vref in abstractSlotValsOfTycons [tycon] do
        match vref.ValReprInfo with
        | Some valReprInfo ->
            let tps, argTysl, retTy, _ = GetValReprTypeInFSharpForm g valReprInfo vref.Type m
            let env = BindTypars g env tps
            for argTys in argTysl do
                for argTy, _ in argTys do
                     CheckTypeNoInnerByrefs cenv env vref.Range argTy
            CheckTypeNoInnerByrefs cenv env vref.Range retTy
        | None -> ()

    // Supported interface may not have byrefs
    tycon.ImmediateInterfaceTypesOfFSharpTycon |> List.iter (CheckTypeNoByrefs cenv env m)

    superOfTycon g tycon |> CheckTypeNoByrefs cenv env m

    if tycon.IsUnionTycon then
        for ucase in tycon.UnionCasesArray do
            CheckAttribs cenv env ucase.Attribs
            ucase.RecdFieldsArray |> Array.iter (CheckRecdField true cenv env tycon)

    // Access checks
    let access = AdjustAccess (IsHiddenTycon env.sigToImplRemapInfo tycon) (fun () -> tycon.CompilationPath) tycon.Accessibility
    let visitType ty = CheckTypeForAccess cenv env (fun () -> tycon.DisplayNameWithStaticParametersAndUnderscoreTypars) access tycon.Range ty

    abstractSlotValsOfTycons [tycon] |> List.iter (typeOfVal >> visitType)

    superOfTycon g tycon |> visitType

    // We do not have to check access of interface implementations.

    if tycon.IsFSharpDelegateTycon then
        match tycon.TypeReprInfo with
        | TFSharpTyconRepr r ->
            match r.fsobjmodel_kind with
            | TFSharpDelegate ss ->
                //ss.ClassTypars
                //ss.MethodTypars
                ss.FormalReturnType |> Option.iter visitType
                ss.FormalParams |> List.iterSquared (fun (TSlotParam(_, ty, _, _, _, _)) -> visitType ty)
            | _ -> ()
        | _ -> ()


    let interfaces =
        AllSuperTypesOfType g cenv.amap tycon.Range AllowMultiIntfInstantiations.Yes ty
            |> List.filter (isInterfaceTy g)

    if tycon.IsFSharpInterfaceTycon then
        List.iter visitType interfaces // Check inherited interface is as accessible

    if not (isRecdOrStructTyconRefAssumedImmutable g tcref) && isRecdOrStructTyconRefReadOnly g m tcref then
        errorR(Error(FSComp.SR.readOnlyAttributeOnStructWithMutableField(), m))

    if cenv.reportErrors then
        if not tycon.IsTypeAbbrev then
            let interfaces =
                GetImmediateInterfacesOfType SkipUnrefInterfaces.Yes g cenv.amap m ty
                |> List.collect (AllSuperTypesOfType g cenv.amap m AllowMultiIntfInstantiations.Yes)
                |> List.filter (isInterfaceTy g)
            CheckMultipleInterfaceInstantiations cenv ty interfaces false m

        // Check fields. We check these late because we have to have first checked that the structs are
        // free of cycles
        if g.langFeatureNullness && g.checkNullness then
            for f in tycon.AllInstanceFieldsAsList do
                let m = f.Range
                // Check if it's marked unsafe
                let zeroInitUnsafe = TryFindFSharpBoolAttribute g g.attrib_DefaultValueAttribute f.FieldAttribs
                if zeroInitUnsafe = Some true then
                    let ty = f.FormalType
                    // If the condition is detected because of a variation in logic introduced because
                    // of nullness checking, then only a warning is emitted.
                    if not (TypeHasDefaultValueNew g m ty) then
                        if not (TypeHasDefaultValue g m ty) then
                            errorR(Error(FSComp.SR.chkValueWithDefaultValueMustHaveDefaultValue(), m))
                        else
                            warning(Error(FSComp.SR.chkValueWithDefaultValueMustHaveDefaultValue(), m))

        // These are the old rules (not g.langFeatureNullness or not g.checkNullness), mistakenly only applied to structs
        elif tycon.IsStructOrEnumTycon then
            for f in tycon.AllInstanceFieldsAsList do
                let m = f.Range
                // Check if it's marked unsafe
                let zeroInitUnsafe = TryFindFSharpBoolAttribute g g.attrib_DefaultValueAttribute f.FieldAttribs
                if zeroInitUnsafe = Some true then
                    if not (TypeHasDefaultValue g m f.FormalType) then
                        errorR(Error(FSComp.SR.chkValueWithDefaultValueMustHaveDefaultValue(), m))

        // Check type abbreviations
        match tycon.TypeAbbrev with
        | None     -> ()
        | Some ty ->
             // Library-defined outref<'T> and inref<'T> contain byrefs on the r.h.s.
             if not g.compilingFSharpCore then
                 CheckForByrefType cenv env ty (fun () -> errorR(Error(FSComp.SR.chkNoByrefInTypeAbbrev(), tycon.Range)))

let CheckEntityDefns cenv env tycons =
    tycons |> List.iter (CheckEntityDefn cenv env)

//--------------------------------------------------------------------------
// check modules
//--------------------------------------------------------------------------

let rec CheckDefnsInModule cenv env mdefs =
    for mdef in mdefs do
        CheckDefnInModule cenv env mdef

and CheckNothingAfterEntryPoint cenv m =
    if cenv.entryPointGiven && cenv.reportErrors then
        errorR(Error(FSComp.SR.chkEntryPointUsage(), m))

and CheckDefnInModule cenv env mdef =
    match mdef with
    | TMDefRec(isRec, _opens, tycons, mspecs, m) ->
        CheckNothingAfterEntryPoint cenv m
        if isRec then BindVals cenv env (allValsOfModDef mdef |> Seq.toList)
        CheckEntityDefns cenv env tycons
        List.iter (CheckModuleSpec cenv env) mspecs
    | TMDefLet(bind, m)  ->
        CheckNothingAfterEntryPoint cenv m
        CheckModuleBinding cenv env bind
        BindVal cenv env bind.Var
    | TMDefOpens _ ->
        ()
    | TMDefDo(e, m)  ->
        CheckNothingAfterEntryPoint cenv m
        CheckNoReraise cenv None e
        CheckExprNoByrefs cenv env e
    | TMDefs defs -> CheckDefnsInModule cenv env defs

and CheckModuleSpec cenv env mbind =
    match mbind with
    | ModuleOrNamespaceBinding.Binding bind ->
        BindVals cenv env (valsOfBinds [bind])
        CheckModuleBinding cenv env bind
    | ModuleOrNamespaceBinding.Module (mspec, rhs) ->
        CheckEntityDefn cenv env mspec
        let env = { env with reflect = env.reflect || HasFSharpAttribute cenv.g cenv.g.attrib_ReflectedDefinitionAttribute mspec.Attribs }
        CheckDefnInModule cenv env rhs

let CheckImplFileContents cenv env implFileTy implFileContents  =
    let rpi, mhi = ComputeRemappingFromImplementationToSignature cenv.g implFileContents implFileTy
    let env = { env with sigToImplRemapInfo = (mkRepackageRemapping rpi, mhi) :: env.sigToImplRemapInfo }
    UpdatePrettyTyparNames.updateModuleOrNamespaceType implFileTy
    CheckDefnInModule cenv env implFileContents

let CheckImplFile (g, amap, reportErrors, infoReader, internalsVisibleToPaths, viewCcu, tcValF, denv, implFileTy, implFileContents, extraAttribs, isLastCompiland: bool*bool, isInternalTestSpanStackReferring) =
    let cenv =
        { g = g
          reportErrors = reportErrors
          boundVals = Dictionary<_, _>(100, HashIdentity.Structural)
          limitVals = Dictionary<_, _>(100, HashIdentity.Structural)
          stackGuard = StackGuard(PostInferenceChecksStackGuardDepth, "CheckImplFile")
          potentialUnboundUsesOfVals = Map.empty
          anonRecdTypes = StampMap.Empty
          usesQuotations = false
          infoReader = infoReader
          internalsVisibleToPaths = internalsVisibleToPaths
          amap = amap
          denv = denv
          viewCcu = viewCcu
          isLastCompiland = isLastCompiland
          isInternalTestSpanStackReferring = isInternalTestSpanStackReferring
          tcVal = tcValF
          entryPointGiven = false}

    // Certain type equality checks go faster if these TyconRefs are pre-resolved.
    // This is because pre-resolving allows tycon equality to be determined by pointer equality on the entities.
    // See primEntityRefEq.
    g.system_Void_tcref.TryDeref |> ignore
    g.byref_tcr.TryDeref |> ignore

    let resolve = function Some(tcref : TyconRef) -> ignore(tcref.TryDeref) | _ -> ()
    resolve g.system_TypedReference_tcref
    resolve g.system_ArgIterator_tcref
    resolve g.system_RuntimeArgumentHandle_tcref

    let env =
        { sigToImplRemapInfo=[]
          quote=false
          boundTyparNames=[]
          argVals = ValMap.Empty
          boundTypars= TyparMap.Empty
          reflect=false
          external=false
          returnScope = 0
          isInAppExpr = false
          resumableCode = Resumable.None }

    CheckImplFileContents cenv env implFileTy implFileContents
    CheckAttribs cenv env extraAttribs

    if cenv.usesQuotations && not (QuotationTranslator.QuotationGenerationScope.ComputeQuotationFormat(g).SupportsDeserializeEx) then
        viewCcu.UsesFSharp20PlusQuotations <- true

    cenv.entryPointGiven, cenv.anonRecdTypes
