// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

module internal FSharp.Compiler.Infos

open System
open Internal.Utilities.Library
open Internal.Utilities.Library.Extras
open FSharp.Compiler
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.DiagnosticsLogger
open FSharp.Compiler.Import
open FSharp.Compiler.Import.Nullness
open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTreeOps
open FSharp.Compiler.TcGlobals
open FSharp.Compiler.Text
open FSharp.Compiler.TypedTree
open FSharp.Compiler.TypedTreeBasics
open FSharp.Compiler.TypedTreeOps
open FSharp.Compiler.TypedTreeOps.DebugPrint
open FSharp.Compiler.TypeHierarchy
open FSharp.Compiler.Xml

#if !NO_TYPEPROVIDERS
open FSharp.Compiler.TypeProviders
open FSharp.Compiler.AbstractIL

#endif

//-------------------------------------------------------------------------
// Predicates and properties on values and members

type ValRef with
    /// Indicates if an F#-declared function or member value is a CLIEvent property compiled as a .NET event
    member x.IsFSharpEventProperty g =
        x.IsMember && CompileAsEvent g x.Attribs && not x.IsExtensionMember

    /// Check if an F#-declared member value is a virtual method
    member vref.IsVirtualMember =
        let flags = vref.MemberInfo.Value.MemberFlags
        flags.IsDispatchSlot || flags.IsOverrideOrExplicitImpl

    /// Check if an F#-declared member value is a dispatch slot
    member vref.IsDispatchSlotMember =
        let membInfo = vref.MemberInfo.Value
        membInfo.MemberFlags.IsDispatchSlot

    /// Check if an F#-declared member value is an 'override' or explicit member implementation
    member vref.IsDefiniteFSharpOverrideMember =
        let membInfo = vref.MemberInfo.Value
        let flags = membInfo.MemberFlags
        not flags.IsDispatchSlot && (flags.IsOverrideOrExplicitImpl || not (isNil membInfo.ImplementedSlotSigs))

    /// Check if an F#-declared member value is an  explicit interface member implementation
    member vref.IsFSharpExplicitInterfaceImplementation g =
        match vref.MemberInfo with
        | None -> false
        | Some membInfo ->
        not membInfo.MemberFlags.IsDispatchSlot &&
        (match membInfo.ImplementedSlotSigs with
         | slotSig :: _ -> isInterfaceTy g slotSig.DeclaringType
         | [] -> false)

    member vref.ImplementedSlotSignatures =
        match vref.MemberInfo with
        | None -> []
        | Some membInfo -> membInfo.ImplementedSlotSigs

//-------------------------------------------------------------------------
// Helper methods associated with using TAST metadata (F# members, values etc.)
// as backing data for MethInfo, PropInfo etc.


#if !NO_TYPEPROVIDERS
/// Get the return type of a provided method, where 'void' is returned as 'None'
let GetCompiledReturnTyOfProvidedMethodInfo amap m (mi: Tainted<ProvidedMethodBase>) =
    let returnType =
        if mi.PUntaint((fun mi -> mi.IsConstructor), m) then
            mi.PApply((fun mi -> nonNull<ProvidedType> mi.DeclaringType), m)
        else mi.Coerce<ProvidedMethodInfo>(m).PApply((fun mi -> mi.ReturnType), m)
    let ty = ImportProvidedType amap m returnType
    if isVoidTy amap.g ty then None else Some ty
#endif

/// The slotsig returned by methInfo.GetSlotSig is in terms of the type parameters on the parent type of the overriding method.
/// Reverse-map the slotsig so it is in terms of the type parameters for the overriding method
let ReparentSlotSigToUseMethodTypars g m ovByMethValRef slotsig =
    match PartitionValRefTypars g ovByMethValRef with
    | Some(_, enclosingTypars, _, _, _) ->
        let parentToMemberInst, _ = mkTyparToTyparRenaming (ovByMethValRef.MemberApparentEntity.Typars m) enclosingTypars
        let res = instSlotSig parentToMemberInst slotsig
        res
    | None ->
        // Note: it appears PartitionValRefTypars should never return 'None'
        slotsig

/// Construct the data representing a parameter in the signature of an abstract method slot
let MakeSlotParam (ty, argInfo: ArgReprInfo) =
    TSlotParam(Option.map textOfId argInfo.Name, ty, false, false, false, argInfo.Attribs)

/// Construct the data representing the signature of an abstract method slot
let MakeSlotSig (nm, ty, ctps, mtps, paraml, retTy) =
    copySlotSig (TSlotSig(nm, ty, ctps, mtps, paraml, retTy))

/// Split the type of an F# member value into
///    - the type parameters associated with method but matching those of the enclosing type
///    - the type parameters associated with a generic method
///    - the return type of the method
///    - the actual type arguments of the enclosing type.
let private AnalyzeTypeOfMemberVal isCSharpExt g (ty, vref: ValRef) =
    let memberAllTypars, _, _, retTy, _ = GetTypeOfMemberInMemberForm g vref
    if isCSharpExt || vref.IsExtensionMember then
        [], memberAllTypars, retTy, []
    else
        let parentTyArgs = argsOfAppTy g ty
        let memberParentTypars, memberMethodTypars = List.splitAt parentTyArgs.Length memberAllTypars
        memberParentTypars, memberMethodTypars, retTy, parentTyArgs

/// Get the object type for a member value which is an extension method  (C#-style or F#-style)
let private GetObjTypeOfInstanceExtensionMethod g (vref: ValRef) =
    let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal vref.Deref
    let _, _, curriedArgInfos, _, _ = GetValReprTypeInCompiledForm g vref.ValReprInfo.Value numEnclosingTypars vref.Type vref.Range
    curriedArgInfos.Head.Head |> fst

/// Get the object type for a member value, which might be a C#-style extension method
let private GetArgInfosOfMember isCSharpExt g (vref: ValRef) =
    if isCSharpExt then
        let numEnclosingTypars = CountEnclosingTyparsOfActualParentOfVal vref.Deref
        let _, _, curriedArgInfos, _, _ = GetValReprTypeInCompiledForm g vref.ValReprInfo.Value numEnclosingTypars vref.Type vref.Range
        [ curriedArgInfos.Head.Tail ]
    else
        ArgInfosOfMember  g vref

/// Combine the type instantiation and generic method instantiation
let private CombineMethInsts ttps mtps tinst minst = (mkTyparInst ttps tinst @ mkTyparInst mtps minst)

/// Work out the instantiation relevant to interpret the backing metadata for a member.
///
/// The 'methTyArgs' is the instantiation of any generic method type parameters (this instantiation is
/// not included in the MethInfo objects, but carried separately).
let private GetInstantiationForMemberVal g isCSharpExt (ty, vref, methTyArgs: TypeInst) =
    let memberParentTypars, memberMethodTypars, _retTy, parentTyArgs = AnalyzeTypeOfMemberVal isCSharpExt g (ty, vref)
    /// In some recursive inference cases involving constraints this may need to be
    /// fixed up - we allow uniform generic recursion but nothing else.
    /// See https://github.com/dotnet/fsharp/issues/3038#issuecomment-309429410
    let methTyArgsFixedUp =
        if methTyArgs.Length < memberMethodTypars.Length then
            methTyArgs @ (List.skip methTyArgs.Length memberMethodTypars |> generalizeTypars)
        else
            methTyArgs
    CombineMethInsts memberParentTypars memberMethodTypars parentTyArgs methTyArgsFixedUp

/// Work out the instantiation relevant to interpret the backing metadata for a property.
let private GetInstantiationForPropertyVal g (ty, vref) =
    let memberParentTypars, memberMethodTypars, _retTy, parentTyArgs = AnalyzeTypeOfMemberVal false g (ty, vref)
    CombineMethInsts memberParentTypars memberMethodTypars parentTyArgs (generalizeTypars memberMethodTypars)

let private HasExternalInit (mref: ILMethodRef) : bool =
    match mref.ReturnType with
    | ILType.Modified(_, cls, _) -> cls.FullName = "System.Runtime.CompilerServices.IsExternalInit"
    | _ -> false

/// Describes the sequence order of the introduction of an extension method. Extension methods that are introduced
/// later through 'open' get priority in overload resolution.
type ExtensionMethodPriority = uint64

//-------------------------------------------------------------------------
// OptionalArgCallerSideValue, OptionalArgInfo

/// The caller-side value for the optional arg, if any
type OptionalArgCallerSideValue =
    | Constant of ILFieldInit
    | DefaultValue
    | MissingValue
    | WrapperForIDispatch
    | WrapperForIUnknown
    | PassByRef of TType * OptionalArgCallerSideValue

/// Represents information about a parameter indicating if it is optional.
type OptionalArgInfo =
    /// The argument is not optional
    | NotOptional

    /// The argument is optional, and is an F# callee-side optional arg
    | CalleeSide

    /// The argument is optional, and is a caller-side .NET optional or default arg.
    /// Note this is correctly termed caller side, even though the default value is optically specified on the callee:
    /// in fact the default value is read from the metadata and passed explicitly to the callee on the caller side.
    | CallerSide of OptionalArgCallerSideValue

    member x.IsOptional =
        match x with
        | CalleeSide | CallerSide  _ -> true
        | NotOptional -> false

    /// Compute the OptionalArgInfo for an IL parameter
    ///
    /// This includes the Visual Basic rules for IDispatchConstant and IUnknownConstant and optional arguments.
    static member FromILParameter g amap m ilScope ilTypeInst (ilParam: ILParameter) =
        if ilParam.IsOptional then
            match ilParam.Default with
            | None ->
                // Do a type-directed analysis of the IL type to determine the default value to pass.
                // The same rules as Visual Basic are applied here.
                let rec analyze ty =
                    if isByrefTy g ty then
                        let ty = destByrefTy g ty
                        PassByRef (ty, analyze ty)
                    elif isObjTyAnyNullness g ty then
                        match ilParam.Marshal with
                        | Some(ILNativeType.IUnknown | ILNativeType.IDispatch | ILNativeType.Interface) -> Constant ILFieldInit.Null
                        | _ ->
                            let attrs = ilParam.CustomAttrs
                            if   TryFindILAttributeOpt g.attrib_IUnknownConstantAttribute attrs then WrapperForIUnknown
                            elif TryFindILAttributeOpt g.attrib_IDispatchConstantAttribute attrs then WrapperForIDispatch
                            else MissingValue
                    else
                        DefaultValue
                                    // See above - the type is imported only in order to be analyzed for optional default value, nullness is irrelevant here.
                CallerSide (analyze (ImportILTypeFromMetadataSkipNullness amap m ilScope ilTypeInst [] ilParam.Type))
            | Some v ->
                CallerSide (Constant v)
        else
            NotOptional

    static member ValueOfDefaultParameterValueAttrib (Attrib (_, _, exprs, _, _, _, _)) =
        let (AttribExpr (_, defaultValueExpr)) = List.head exprs
        match defaultValueExpr with
        | Expr.Const _ -> Some defaultValueExpr
        | _ -> None
    
    static member FieldInitForDefaultParameterValueAttrib attrib =
        match OptionalArgInfo.ValueOfDefaultParameterValueAttrib attrib with
        | Some (Expr.Const (ConstToILFieldInit fi, _, _)) -> Some fi
        | _ -> None

type CallerInfo =
    | NoCallerInfo
    | CallerLineNumber
    | CallerMemberName
    | CallerFilePath

    override x.ToString() = sprintf "%+A" x

[<RequireQualifiedAccess>]
type ReflectedArgInfo =
    | None
    | Quote of bool
    member x.AutoQuote = match x with None -> false | Quote _ -> true

//-------------------------------------------------------------------------
// ParamNameAndType, ParamData

/// Partial information about a parameter returned for use by the Language Service
[<NoComparison; NoEquality>]
type ParamNameAndType =
    | ParamNameAndType of Ident option * TType

    static member FromArgInfo (ty, argInfo : ArgReprInfo) = ParamNameAndType(argInfo.Name, ty)
    static member FromMember isCSharpExtMem g vref = GetArgInfosOfMember isCSharpExtMem g vref |> List.mapSquared ParamNameAndType.FromArgInfo
    static member Instantiate inst p = let (ParamNameAndType(nm, ty)) = p in ParamNameAndType(nm, instType inst ty)
    static member InstantiateCurried inst paramTypes = paramTypes |> List.mapSquared (ParamNameAndType.Instantiate inst)

/// Full information about a parameter returned for use by the type checker and language service.
[<NoComparison; NoEquality>]
type ParamData =
    ParamData of
        isParamArray: bool *
        isInArg: bool *
        isOut: bool *
        optArgInfo: OptionalArgInfo *
        callerInfo: CallerInfo *
        nameOpt: Ident option *
        reflArgInfo: ReflectedArgInfo *
        ttype: TType

type ParamAttribs = ParamAttribs of isParamArrayArg: bool * isInArg: bool * isOutArg: bool * optArgInfo: OptionalArgInfo * callerInfo: CallerInfo * reflArgInfo: ReflectedArgInfo

let CrackParamAttribsInfo g (ty: TType, argInfo: ArgReprInfo) =
    let isParamArrayArg = HasFSharpAttribute g g.attrib_ParamArrayAttribute argInfo.Attribs
    let reflArgInfo =
        match TryFindFSharpBoolAttributeAssumeFalse  g g.attrib_ReflectedDefinitionAttribute argInfo.Attribs  with
        | Some b -> ReflectedArgInfo.Quote b
        | None -> ReflectedArgInfo.None
    let isOutArg = (HasFSharpAttribute g g.attrib_OutAttribute argInfo.Attribs && isByrefTy g ty) || isOutByrefTy g ty
    let isInArg = (HasFSharpAttribute g g.attrib_InAttribute argInfo.Attribs && isByrefTy g ty) || isInByrefTy g ty
    let isCalleeSideOptArg = HasFSharpAttribute g g.attrib_OptionalArgumentAttribute argInfo.Attribs
    let isCallerSideOptArg = HasFSharpAttributeOpt g g.attrib_OptionalAttribute argInfo.Attribs
    let optArgInfo =
        if isCalleeSideOptArg then
            CalleeSide
        elif isCallerSideOptArg then
            let defaultParameterValueAttribute = TryFindFSharpAttributeOpt g g.attrib_DefaultParameterValueAttribute argInfo.Attribs
            match defaultParameterValueAttribute with
            | None ->
                // Do a type-directed analysis of the type to determine the default value to pass.
                // Similar rules as OptionalArgInfo.FromILParameter are applied here, except for the COM and byref-related stuff.
                CallerSide (if isObjTyAnyNullness g ty then MissingValue else DefaultValue)
            | Some attr ->
                let defaultValue = OptionalArgInfo.ValueOfDefaultParameterValueAttrib attr
                match defaultValue with
                | Some (Expr.Const (_, m, ty2)) when not (typeEquiv g ty2 ty) ->
                    // the type of the default value does not match the type of the argument.
                    // Emit a warning, and ignore the DefaultParameterValue argument altogether.
                    warning(Error(FSComp.SR.DefaultParameterValueNotAppropriateForArgument(), m))
                    NotOptional
                | Some (Expr.Const (ConstToILFieldInit fi, _, _)) ->
                    // Good case - all is well.
                    CallerSide (Constant fi)
                | _ ->
                    // Default value is not appropriate, i.e. not a constant.
                    // Compiler already gives an error in that case, so just ignore here.
                    NotOptional
        else NotOptional

    let isCallerLineNumberArg = HasFSharpAttribute g g.attrib_CallerLineNumberAttribute argInfo.Attribs
    let isCallerFilePathArg = HasFSharpAttribute g g.attrib_CallerFilePathAttribute argInfo.Attribs
    let isCallerMemberNameArg = HasFSharpAttribute g g.attrib_CallerMemberNameAttribute argInfo.Attribs

    let callerInfo =
        match isCallerLineNumberArg, isCallerFilePathArg, isCallerMemberNameArg with
        | false, false, false -> NoCallerInfo
        | true, false, false -> CallerLineNumber
        | false, true, false -> CallerFilePath
        | false, false, true -> CallerMemberName
        | false, true, true -> 
            match TryFindFSharpAttribute g g.attrib_CallerMemberNameAttribute argInfo.Attribs with
            | Some(Attrib(_, _, _, _, _, _, callerMemberNameAttributeRange)) ->
                warning(Error(FSComp.SR.CallerMemberNameIsOverridden(argInfo.Name.Value.idText), callerMemberNameAttributeRange))
                CallerFilePath
            | _ -> failwith "Impossible"
        | _, _, _ ->
            // if multiple caller info attributes are specified, pick the "wrong" one here
            // so that we get an error later
            match tryDestOptionTy g ty with
            | ValueSome optTy when typeEquiv g g.int32_ty optTy -> CallerFilePath
            | _ -> CallerLineNumber

    ParamAttribs(isParamArrayArg, isInArg, isOutArg, optArgInfo, callerInfo, reflArgInfo)

#if !NO_TYPEPROVIDERS

type ILFieldInit with

    /// Compute the ILFieldInit for the given provided constant value for a provided enum type.
    static member FromProvidedObj m (v: obj MaybeNull) =
        match v with
        | Null -> ILFieldInit.Null
        | NonNull v ->
            let objTy = v.GetType()
            let v = if objTy.IsEnum then !!(!!objTy.GetField("value__")).GetValue v else v
            match v with
            | :? single as i -> ILFieldInit.Single i
            | :? double as i -> ILFieldInit.Double i
            | :? bool as i -> ILFieldInit.Bool i
            | :? char as i -> ILFieldInit.Char (uint16 i)
            | :? string as i -> ILFieldInit.String i
            | :? sbyte as i -> ILFieldInit.Int8 i
            | :? byte as i -> ILFieldInit.UInt8 i
            | :? int16 as i -> ILFieldInit.Int16 i
            | :? uint16 as i -> ILFieldInit.UInt16 i
            | :? int as i -> ILFieldInit.Int32 i
            | :? uint32 as i -> ILFieldInit.UInt32 i
            | :? int64 as i -> ILFieldInit.Int64 i
            | :? uint64 as i -> ILFieldInit.UInt64 i
            | _ -> 
                let txt = match v with | null -> "?" | v -> try !!v.ToString() with _ -> "?"
                error(Error(FSComp.SR.infosInvalidProvidedLiteralValue(txt), m))


/// Compute the OptionalArgInfo for a provided parameter.
///
/// This is the same logic as OptionalArgInfoOfILParameter except we do not apply the
/// Visual Basic rules for IDispatchConstant and IUnknownConstant to optional
/// provided parameters.
let OptionalArgInfoOfProvidedParameter (amap: ImportMap) m (provParam : Tainted<ProvidedParameterInfo>) =
    let g = amap.g
    if provParam.PUntaint((fun p -> p.IsOptional), m) then
        match provParam.PUntaint((fun p ->  p.HasDefaultValue), m) with
        | false ->
            // Do a type-directed analysis of the IL type to determine the default value to pass.
            let rec analyze ty =
                if isByrefTy g ty then
                    let ty = destByrefTy g ty
                    PassByRef (ty, analyze ty)
                elif isObjTyAnyNullness g ty then MissingValue
                else  DefaultValue

            let paramTy = ImportProvidedType amap m (provParam.PApply((fun p -> p.ParameterType), m))
            CallerSide (analyze paramTy)
        | _ ->
            let v = provParam.PUntaint((fun p ->  p.RawDefaultValue), m)
            CallerSide (Constant (ILFieldInit.FromProvidedObj m v))
    else
        NotOptional

/// Compute the ILFieldInit for the given provided constant value for a provided enum type.
let GetAndSanityCheckProviderMethod m (mi: Tainted<'T :> ProvidedMemberInfo>) (get : 'T -> ProvidedMethodInfo MaybeNull) err = 
    match mi.PApply((fun mi -> (get mi :> ProvidedMethodBase MaybeNull)),m) with 
    | Tainted.Null -> error(Error(err(mi.PUntaint((fun mi -> mi.Name),m),mi.PUntaint((fun mi -> (nonNull mi.DeclaringType).Name), m)), m))
    | Tainted.NonNull meth -> meth

/// Try to get an arbitrary ProvidedMethodInfo associated with a property.
let ArbitraryMethodInfoOfPropertyInfo (pi: Tainted<ProvidedPropertyInfo>) m =
    if pi.PUntaint((fun pi -> pi.CanRead), m) then
        GetAndSanityCheckProviderMethod m pi (fun pi -> pi.GetGetMethod()) FSComp.SR.etPropertyCanReadButHasNoGetter
    elif pi.PUntaint((fun pi -> pi.CanWrite), m) then
        GetAndSanityCheckProviderMethod m pi (fun pi -> pi.GetSetMethod()) FSComp.SR.etPropertyCanWriteButHasNoSetter
    else
        error(Error(FSComp.SR.etPropertyNeedsCanWriteOrCanRead(pi.PUntaint((fun mi -> mi.Name), m), pi.PUntaint((fun mi -> (nonNull<ProvidedType> mi.DeclaringType).Name), m)), m))

#endif


/// Describes an F# use of an IL type, including the type instantiation associated with the type at a particular usage point.
///
/// This is really just 1:1 with the subset ot TType which result from building types using IL type definitions.
[<NoComparison; NoEquality>]
type ILTypeInfo =
    /// ILTypeInfo (tyconRef, ilTypeRef, typeArgs, ilTypeDef).
    | ILTypeInfo of TcGlobals * TType * ILTypeRef * ILTypeDef

    member x.TcGlobals = let (ILTypeInfo(g, _, _, _)) = x in g

    member x.ILTypeRef = let (ILTypeInfo(_, _, tref, _)) = x in tref

    member x.RawMetadata = let (ILTypeInfo(_, _, _, tdef))  = x in tdef

    member x.ToType = let (ILTypeInfo(_, ty, _, _)) = x in ty

    /// Get the compiled nominal type. In the case of tuple types, this is a .NET tuple type
    member x.ToAppType = convertToTypeWithMetadataIfPossible x.TcGlobals x.ToType

    member x.TyconRefOfRawMetadata = tcrefOfAppTy x.TcGlobals x.ToAppType

    member x.TypeInstOfRawMetadata = argsOfAppTy x.TcGlobals x.ToAppType

    member x.ILScopeRef = x.ILTypeRef.Scope

    member x.Name     = x.ILTypeRef.Name

    member x.IsValueType = x.RawMetadata.IsStructOrEnum

    /// Indicates if the type is marked with the [<IsReadOnly>] attribute.
    member x.IsReadOnly (g: TcGlobals) =
        x.RawMetadata.CustomAttrs
        |> TryFindILAttribute g.attrib_IsReadOnlyAttribute

    member x.Instantiate inst =
        let (ILTypeInfo(g, ty, tref, tdef)) = x
        ILTypeInfo(g, instType inst ty, tref, tdef)

    member x.NullableAttributes = AttributesFromIL(x.RawMetadata.MetadataIndex,x.RawMetadata.CustomAttrsStored)

    member x.NullableClassSource = FromClass(x.NullableAttributes)

    static member FromType g ty =
        if isAnyTupleTy g ty then
            // When getting .NET metadata for the properties and methods
            // of an F# tuple type, use the compiled nominal type, which is a .NET tuple type
            let metadataTy = convertToTypeWithMetadataIfPossible g ty
            assert (isILAppTy g metadataTy)
            let metadataTyconRef = tcrefOfAppTy g metadataTy
            let (TILObjectReprData(scoref, enc, tdef)) = metadataTyconRef.ILTyconInfo
            let metadataILTypeRef = mkRefForNestedILTypeDef scoref (enc, tdef)
            ILTypeInfo(g, ty, metadataILTypeRef, tdef)
        elif isILAppTy g ty then
            let tcref = tcrefOfAppTy g ty
            let (TILObjectReprData(scoref, enc, tdef)) = tcref.ILTyconInfo
            let tref = mkRefForNestedILTypeDef scoref (enc, tdef)
            ILTypeInfo(g, ty, tref, tdef)
        else
            failwith ("ILTypeInfo.FromType - no IL metadata for type" + System.Environment.StackTrace)

[<NoComparison; NoEquality>]
type ILMethParentTypeInfo =
    | IlType of ILTypeInfo
    | CSharpStyleExtension of declaring:TyconRef * apparent:TType
    
    member x.ToType =
        match x with
        | IlType x -> x.ToType
        | CSharpStyleExtension(apparent=x) -> x

/// Describes an F# use of an IL method.
[<NoComparison; NoEquality>]
type ILMethInfo =
    /// Describes an F# use of an IL method.
    ///
    /// If ilDeclaringTyconRefOpt is 'Some' then this is an F# use of an C#-style extension method.
    /// If ilDeclaringTyconRefOpt is 'None' then ilApparentType is an IL type definition.
    | ILMethInfo of g: TcGlobals * ilType:ILMethParentTypeInfo * ilMethodDef: ILMethodDef * ilGenericMethodTyArgs: Typars

    member x.TcGlobals = match x with ILMethInfo(g, _, _, _) -> g

    /// Get the apparent declaring type of the method as an F# type.
    /// If this is a C#-style extension method then this is the type which the method
    /// appears to extend. This may be a variable type.
    member x.ApparentEnclosingType = match x with ILMethInfo(_, ty, _, _) -> ty.ToType

    /// Like ApparentEnclosingType but use the compiled nominal type if this is a method on a tuple type
    member x.ApparentEnclosingAppType = convertToTypeWithMetadataIfPossible x.TcGlobals x.ApparentEnclosingType

    /// Get the declaring type associated with an extension member, if any.
    member x.ILExtensionMethodDeclaringTyconRef = 
        match x with
        | ILMethInfo(ilType=CSharpStyleExtension(declaring= x)) -> Some x
        | _ -> None

    /// Get the Abstract IL metadata associated with the method.
    member x.RawMetadata = match x with ILMethInfo(_, _, md, _) -> md

    /// Get the formal method type parameters associated with a method.
    member x.FormalMethodTypars = match x with ILMethInfo(_, _, _, fmtps) -> fmtps

    /// Get the IL name of the method
    member x.ILName       = x.RawMetadata.Name

    /// Indicates if the method is an extension method
    member x.IsILExtensionMethod = 
        match x with
        | ILMethInfo(ilType=CSharpStyleExtension _) -> true
        | _ -> false

    /// Get the declaring type of the method. If this is an C#-style extension method then this is the IL type
    /// holding the static member that is the extension method.
    member x.DeclaringTyconRef   =
        match x.ILExtensionMethodDeclaringTyconRef with
        | Some tcref -> tcref
        | None -> tcrefOfAppTy x.TcGlobals  x.ApparentEnclosingAppType

    /// Get the instantiation of the declaring type of the method.
    /// If this is an C#-style extension method then this is empty because extension members
    /// are never in generic classes.
    member x.DeclaringTypeInst   =
        if x.IsILExtensionMethod then []
        else argsOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    /// Get the Abstract IL scope information associated with interpreting the Abstract IL metadata that backs this method.
    member x.MetadataScope   = x.DeclaringTyconRef.CompiledRepresentationForNamedType.Scope

    /// Get the Abstract IL metadata corresponding to the parameters of the method.
    /// If this is an C#-style extension method then drop the object argument.
    member x.ParamMetadata =
        let ps = x.RawMetadata.Parameters
        if x.IsILExtensionMethod then List.tail ps else ps

    /// Get the number of parameters of the method
    member x.NumParams = x.ParamMetadata.Length

    /// Indicates if the method is a constructor
    member x.IsConstructor = x.RawMetadata.IsConstructor

    /// Indicates if the method is a class initializer.
    member x.IsClassConstructor = x.RawMetadata.IsClassInitializer

    /// Indicates if the method has protected accessibility,
    member x.IsProtectedAccessibility =
        let md = x.RawMetadata
        not md.IsConstructor &&
        not md.IsClassInitializer &&
        (md.Access = ILMemberAccess.Family || md.Access = ILMemberAccess.FamilyOrAssembly)

    /// Indicates if the IL method is marked virtual.
    member x.IsVirtual = x.RawMetadata.IsVirtual

    /// Indicates if the IL method is marked final.
    member x.IsFinal = x.RawMetadata.IsFinal

    /// Indicates if the IL method is marked abstract.
    member x.IsAbstract = x.RawMetadata.IsAbstract

    /// Does it appear to the user as a static method?
    member x.IsStatic =
        not x.IsILExtensionMethod &&  // all C#-declared extension methods are instance
        x.RawMetadata.CallingConv.IsStatic

    /// Does it have the .NET IL 'newslot' flag set, and is also a virtual?
    member x.IsNewSlot = x.RawMetadata.IsNewSlot

    /// Does it appear to the user as an instance method?
    member x.IsInstance = not x.IsConstructor &&  not x.IsStatic

    member x.NullableFallback = 
        let raw = x.RawMetadata
        let classAttrs = 
            match x with
            | ILMethInfo(ilType=CSharpStyleExtension(declaring= t)) when t.IsILTycon -> AttributesFromIL(t.ILTyconRawMetadata.MetadataIndex,t.ILTyconRawMetadata.CustomAttrsStored)
            // C#-style extension defined in F# -> we do not support manually adding NullableContextAttribute by F# users.
            | ILMethInfo(ilType=CSharpStyleExtension _)  -> AttributesFromIL(0,Given(ILAttributes.Empty))
            | ILMethInfo(ilType=IlType(t)) -> t.NullableAttributes

        FromMethodAndClass(AttributesFromIL(raw.MetadataIndex,raw.CustomAttrsStored),classAttrs)

    member x.GetNullness(p:ILParameter) = {DirectAttributes = AttributesFromIL(p.MetadataIndex,p.CustomAttrsStored); Fallback = x.NullableFallback}

    /// Get the argument types of the IL method. If this is an C#-style extension method
    /// then drop the object argument.
    member x.GetParamTypes(amap, m, minst) =
        x.ParamMetadata |> List.map (fun p -> ImportParameterTypeFromMetadata amap m (x.GetNullness(p)) p.Type x.MetadataScope x.DeclaringTypeInst minst)

    /// Get all the argument types of the IL method. Include the object argument even if this is
    /// an C#-style extension method.
    member x.GetRawArgTypes(amap, m, minst) =
        x.RawMetadata.Parameters |> List.map (fun p -> ImportParameterTypeFromMetadata amap m (x.GetNullness(p)) p.Type x.MetadataScope x.DeclaringTypeInst minst)

    /// Get info about the arguments of the IL method. If this is an C#-style extension method then
    /// drop the object argument.
    ///
    /// Any type parameters of the enclosing type are instantiated in the type returned.
    member x.GetParamNamesAndTypes(amap, m, minst) =
        let scope = x.MetadataScope
        let tinst = x.DeclaringTypeInst
        x.ParamMetadata |> List.map (fun p -> ParamNameAndType(Option.map (mkSynId m) p.Name, ImportParameterTypeFromMetadata amap m (x.GetNullness(p))  p.Type scope tinst minst) )

    /// Get a reference to the method (dropping all generic instantiations), as an Abstract IL ILMethodRef.
    member x.ILMethodRef =
        let mref = mkRefToILMethod (x.DeclaringTyconRef.CompiledRepresentationForNamedType, x.RawMetadata)
        rescopeILMethodRef x.MetadataScope mref

    /// Indicates if the method is marked as a DllImport (a PInvoke). This is done by looking at the IL custom attributes on
    /// the method.
    member x.IsDllImport (g: TcGlobals) =
        match g.attrib_DllImportAttribute with
        | None -> false
        | Some attr ->
            x.RawMetadata.CustomAttrs
            |> TryFindILAttribute attr

    /// Indicates if the method is marked with the [<IsReadOnly>] attribute. This is done by looking at the IL custom attributes on
    /// the method.
    member x.IsReadOnly (g: TcGlobals) =
        x.RawMetadata.CustomAttrs
        |> TryFindILAttribute g.attrib_IsReadOnlyAttribute

    /// Get the (zero or one) 'self'/'this'/'object' arguments associated with an IL method.
    /// An instance extension method returns one object argument.
    member x.GetObjArgTypes(amap, m, minst) =    
        // All C#-style extension methods are instance. We have to re-read the 'obj' type w.r.t. the
        // method instantiation.
        if x.IsILExtensionMethod then
            let p = x.RawMetadata.Parameters.Head
            let nullableSource = {DirectAttributes = AttributesFromIL(p.MetadataIndex,p.CustomAttrsStored); Fallback = x.NullableFallback}
            [ ImportParameterTypeFromMetadata amap m nullableSource p.Type x.MetadataScope x.DeclaringTypeInst minst ]
        else if x.IsInstance then
            [ x.ApparentEnclosingType ]
        else
            []

    /// Get the compiled return type of the method, where 'void' is None.
    member x.GetCompiledReturnType (amap, m, minst) =
        let ilReturn = x.RawMetadata.Return
        let nullableSource = {DirectAttributes = AttributesFromIL(ilReturn.MetadataIndex,ilReturn.CustomAttrsStored); Fallback = x.NullableFallback}
        ImportReturnTypeFromMetadata amap m nullableSource ilReturn.Type x.MetadataScope x.DeclaringTypeInst minst

    /// Get the F# view of the return type of the method, where 'void' is 'unit'.
    member x.GetFSharpReturnType (amap, m, minst) =      
        x.GetCompiledReturnType(amap, m, minst)
        |> GetFSharpViewOfReturnType amap.g


/// Describes an F# use of a method
[<System.Diagnostics.DebuggerDisplay("{DebuggerDisplayName}")>]
[<NoComparison; NoEquality>]
type MethInfo =
    /// Describes a use of a method declared in F# code and backed by F# metadata.
    | FSMeth of tcGlobals: TcGlobals * enclosingType: TType * valRef: ValRef  * extensionMethodPriority: ExtensionMethodPriority option

    /// Describes a use of a method backed by Abstract IL # metadata
    | ILMeth of tcGlobals: TcGlobals * ilMethInfo: ILMethInfo * extensionMethodPriority: ExtensionMethodPriority option

    /// A pseudo-method used by F# typechecker to treat Object.ToString() of known types as returning regular string, not `string?` as in the BCL
    | MethInfoWithModifiedReturnType of original:MethInfo * modifiedReturnType: TType

    /// Describes a use of a pseudo-method corresponding to the default constructor for a .NET struct type
    | DefaultStructCtor of tcGlobals: TcGlobals * structTy: TType

#if !NO_TYPEPROVIDERS
    /// Describes a use of a method backed by provided metadata
    | ProvidedMeth of amap: ImportMap * methodBase: Tainted<ProvidedMethodBase> * extensionMethodPriority: ExtensionMethodPriority option * m: range
#endif

    /// Get the enclosing type of the method info.
    ///
    /// If this is an extension member, then this is the apparent parent, i.e. the type the method appears to extend.
    /// This may be a variable type.
    member x.ApparentEnclosingType =
        match x with
        | ILMeth(_, ilminfo, _) -> ilminfo.ApparentEnclosingType
        | FSMeth(_, ty, _, _) -> ty
        | MethInfoWithModifiedReturnType(mi, _) -> mi.ApparentEnclosingType
        | DefaultStructCtor(_, ty) -> ty
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, mi, _, m) ->
              ImportProvidedType amap m (mi.PApply((fun mi -> nonNull<ProvidedType> mi.DeclaringType), m))
#endif

    /// Get the enclosing type of the method info, using a nominal type for tuple types
    member x.ApparentEnclosingAppType =
        convertToTypeWithMetadataIfPossible x.TcGlobals x.ApparentEnclosingType

    member x.ApparentEnclosingTyconRef =
        tcrefOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    /// Get the declaring type or module holding the method. If this is an C#-style extension method then this is the type
    /// holding the static member that is the extension method. If this is an F#-style extension method it is the logical module
    /// holding the value for the extension method.
    member x.DeclaringTyconRef   =
        match x with
        | ILMeth(_, ilminfo, _) when x.IsExtensionMember  -> ilminfo.DeclaringTyconRef
        | FSMeth(_, _, vref, _) when x.IsExtensionMember && vref.HasDeclaringEntity -> vref.DeclaringEntity
        | MethInfoWithModifiedReturnType(mi, _) -> mi.DeclaringTyconRef
        | _ -> x.ApparentEnclosingTyconRef

    /// Get the information about provided static parameters, if any
    member x.ProvidedStaticParameterInfo =
        match x with
        | ILMeth _ -> None
        | FSMeth _  -> None
        | MethInfoWithModifiedReturnType _ -> None
#if !NO_TYPEPROVIDERS
        | ProvidedMeth (_, mb, _, m) ->
            let staticParams = mb.PApplyWithProvider((fun (mb, provider) -> mb.GetStaticParametersForMethod provider), range=m)
            let staticParams = staticParams.PApplyArray(id, "GetStaticParametersForMethod", m)
            match staticParams with
            | [| |] -> None
            | _ -> Some (mb, staticParams)
#endif
        | DefaultStructCtor _ -> None

    /// Get the extension method priority of the method, if it has one.
    member x.ExtensionMemberPriorityOption =
        match x with
        | ILMeth(_, _, pri) -> pri
        | FSMeth(_, _, _, pri) -> pri
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, _, pri, _) -> pri
#endif
        | MethInfoWithModifiedReturnType(mi, _) -> mi.ExtensionMemberPriorityOption
        | DefaultStructCtor _ -> None

    /// Get the extension method priority of the method. If it is not an extension method
    /// then use the highest possible value since non-extension methods always take priority
    /// over extension members.
    member x.ExtensionMemberPriority = defaultArg x.ExtensionMemberPriorityOption UInt64.MaxValue

    /// Get the method name in DebuggerDisplayForm
    member x.DebuggerDisplayName =
        match x with
        | ILMeth(_, y, _) -> y.DeclaringTyconRef.DisplayNameWithStaticParametersAndUnderscoreTypars + "::" + y.ILName
        | FSMeth(_, AbbrevOrAppTy(tcref, _), vref, _) -> tcref.DisplayNameWithStaticParametersAndUnderscoreTypars + "::" + vref.LogicalName
        | FSMeth(_, _, vref, _) -> "??::" + vref.LogicalName
        | MethInfoWithModifiedReturnType(mi, returnTy) ->  mi.DebuggerDisplayName + $"({returnTy.DebugText})"
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> "ProvidedMeth: " + mi.PUntaint((fun mi -> mi.Name), m)
#endif
        | DefaultStructCtor _ -> ".ctor"

    /// Get the method name in LogicalName form, i.e. the name as it would be stored in .NET metadata
    member x.LogicalName =
        match x with
        | ILMeth(_, y, _) -> y.ILName
        | FSMeth(_, _, vref, _) -> vref.LogicalName
        | MethInfoWithModifiedReturnType(mi, _) -> mi.LogicalName
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.Name), m)
#endif
        | DefaultStructCtor _ -> ".ctor"

     /// Get the method name in DisplayName form
    member x.DisplayName =
        match x with
        | FSMeth(_, _, vref, _) -> vref.DisplayName
        | _ -> x.LogicalName |> PrettyNaming.ConvertValLogicalNameToDisplayName false

     /// Get the method name in DisplayName form
    member x.DisplayNameCore =
        match x with
        | FSMeth(_, _, vref, _) -> vref.DisplayNameCore
        | _ -> x.LogicalName |> PrettyNaming.ConvertValLogicalNameToDisplayNameCore

     /// Indicates if this is a method defined in this assembly with an internal XML comment
    member x.HasDirectXmlComment =
        match x with
        | FSMeth(g, _, vref, _) -> valRefInThisAssembly g.compilingFSharpCore vref
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> true
#endif
        | _ -> false

    override x.ToString() =  x.ApparentEnclosingType.ToString() + "::" + x.LogicalName

    /// Get the actual type instantiation of the declaring type associated with this use of the method.
    ///
    /// For extension members this is empty (the instantiation of the declaring type).
    member x.DeclaringTypeInst =
        if x.IsExtensionMember then [] else argsOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    /// Get the TcGlobals value that governs the method declaration
    member x.TcGlobals =
        match x with
        | ILMeth(g, _, _) -> g
        | FSMeth(g, _, _, _) -> g
        | MethInfoWithModifiedReturnType(mi, _) -> mi.TcGlobals
        | DefaultStructCtor (g, _) -> g
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, _, _, _) -> amap.g
#endif

    /// Get the formal generic method parameters for the method as a list of type variables.
    ///
    /// For an extension method this includes all type parameters, even if it is extending a generic type.
    member x.FormalMethodTypars =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.FormalMethodTypars
        | FSMeth(g, _, vref, _) ->
            let ty = x.ApparentEnclosingAppType
            let _, memberMethodTypars, _, _ = AnalyzeTypeOfMemberVal x.IsCSharpStyleExtensionMember g (ty, vref)
            memberMethodTypars
        | MethInfoWithModifiedReturnType(mi, _) -> mi.FormalMethodTypars
        | DefaultStructCtor _ -> []
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> [] // There will already have been an error if there are generic parameters here.
#endif

    /// Get the formal generic method parameters for the method as a list of variable types.
    member x.FormalMethodInst = generalizeTypars x.FormalMethodTypars

    member x.FormalMethodTyparInst = mkTyparInst x.FormalMethodTypars x.FormalMethodInst

    /// Get the XML documentation associated with the method
    member x.XmlDoc =
        match x with
        | ILMeth _ -> XmlDoc.Empty
        | FSMeth(_, _, vref, _) -> vref.XmlDoc
        | MethInfoWithModifiedReturnType(mi, _) -> mi.XmlDoc
        | DefaultStructCtor _ -> XmlDoc.Empty
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m)->
            let lines = mi.PUntaint((fun mix -> (mix :> IProvidedCustomAttributeProvider).GetXmlDocAttributes(mi.TypeProvider.PUntaintNoFailure id)), m)
            XmlDoc (lines, m)
#endif

    /// Try to get an arbitrary F# ValRef associated with the member. This is to determine if the member is virtual, amongst other things.
    member x.ArbitraryValRef =
        match x with
        | FSMeth(_g, _, vref, _) -> Some vref
        | MethInfoWithModifiedReturnType(mi, _) -> mi.ArbitraryValRef
        | _ -> None

    /// Get a list of argument-number counts, one count for each set of curried arguments.
    ///
    /// For an extension member, drop the 'this' argument.
    member x.NumArgs =
        match x with
        | ILMeth(_, ilminfo, _) -> [ilminfo.NumParams]
        | FSMeth(g, _, vref, _) -> GetArgInfosOfMember x.IsCSharpStyleExtensionMember g vref |> List.map List.length
        | MethInfoWithModifiedReturnType(mi, _) -> mi.NumArgs
        | DefaultStructCtor _ -> [0]
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> [mi.PApplyArray((fun mi -> mi.GetParameters()),"GetParameters", m).Length] // Why is this a list? Answer: because the method might be curried
#endif

    /// Indicates if the property is a IsABC union case tester implied by a union case definition
    member x.IsUnionCaseTester =
        let tcref = x.ApparentEnclosingTyconRef
        tcref.IsUnionTycon &&
        x.LogicalName.StartsWithOrdinal("get_Is") &&
        match x.ArbitraryValRef with 
        | Some v -> v.IsImplied
        | None -> false

    member x.IsCurried = x.NumArgs.Length > 1

    /// Does the method appear to the user as an instance method?
    member x.IsInstance =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsInstance
        | FSMeth(_, _, vref, _) -> vref.IsInstanceMember || x.IsCSharpStyleExtensionMember
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsInstance
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> not mi.IsConstructor && not mi.IsStatic), m)
#endif

    /// Get the number of generic method parameters for a method.
    /// For an extension method this includes all type parameters, even if it is extending a generic type.
    member x.GenericArity =  x.FormalMethodTypars.Length

    member x.IsProtectedAccessibility =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsProtectedAccessibility
        | FSMeth _ -> false
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsProtectedAccessibility
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsFamily), m)
#endif

    member x.IsVirtual =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsVirtual
        | FSMeth(_, _, vref, _) -> vref.IsVirtualMember
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsVirtual
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsVirtual), m)
#endif

    member x.IsConstructor =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsConstructor
        | FSMeth(_g, _, vref, _) -> (vref.MemberInfo.Value.MemberFlags.MemberKind = SynMemberKind.Constructor)
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsConstructor
        | DefaultStructCtor _ -> true
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsConstructor), m)
#endif

    member x.IsClassConstructor =
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsClassConstructor
        | FSMeth(_, _, vref, _) ->
             match vref.TryDeref with
             | ValueSome x -> x.IsClassConstructor
             | _ -> false
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsClassConstructor
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsConstructor && mi.IsStatic), m) // Note: these are never public anyway
#endif

    member x.IsDispatchSlot =
        match x with
        | ILMeth(_g, ilmeth, _) -> ilmeth.IsVirtual
        | FSMeth(_, _, vref, _) -> vref.MemberInfo.Value.MemberFlags.IsDispatchSlot
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsDispatchSlot
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> x.IsVirtual // Note: follow same implementation as ILMeth
#endif


    member x.IsFinal =
        not x.IsVirtual ||
        match x with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsFinal
        | FSMeth(_g, _, _vref, _) -> false
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsFinal
        | DefaultStructCtor _ -> true
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsFinal), m)
#endif

    // This means 'is this particular MethInfo one that doesn't provide an implementation?'.
    // For F# methods, this is 'true' for the MethInfos corresponding to 'abstract' declarations,
    // and false for the (potentially) matching 'default' implementation MethInfos that eventually
    // provide an implementation for the dispatch slot.
    //
    // For IL methods, this is 'true' for abstract methods, and 'false' for virtual methods
    member minfo.IsAbstract =
        match minfo with
        | ILMeth(_, ilmeth, _) -> ilmeth.IsAbstract
        | FSMeth(g, _, vref, _)  -> isInterfaceTy g minfo.ApparentEnclosingType  || vref.IsDispatchSlotMember
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsAbstract
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsAbstract), m)
#endif

    member x.IsNewSlot =
        (x.IsVirtual &&
          (match x with
           | ILMeth(_, x, _) -> x.IsNewSlot || (isInterfaceTy x.TcGlobals x.ApparentEnclosingType && not x.IsFinal)
           | FSMeth(_, _, vref, _) -> vref.IsDispatchSlotMember
           | MethInfoWithModifiedReturnType(mi, _) -> mi.IsNewSlot
#if !NO_TYPEPROVIDERS
           | ProvidedMeth(_, mi, _, m) -> mi.PUntaint((fun mi -> mi.IsHideBySig), m) // REVIEW: Check this is correct
#endif
           | DefaultStructCtor _ -> false))

    /// Indicates if this is an IL method.
    member x.IsILMethod =
        match x with
        | ILMeth _ -> true
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsILMethod
        | _ -> false

    /// Check if this method is an explicit implementation of an interface member
    member x.IsFSharpExplicitInterfaceImplementation =
        match x with
        | ILMeth _ -> false
        | FSMeth(g, _, vref, _) -> vref.IsFSharpExplicitInterfaceImplementation g
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsFSharpExplicitInterfaceImplementation
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> false
#endif

    /// Check if this method is marked 'override' and thus definitely overrides another method.
    member x.IsDefiniteFSharpOverride =
        match x with
        | ILMeth _ -> false
        | FSMeth(_, _, vref, _) -> vref.IsDefiniteFSharpOverrideMember
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsDefiniteFSharpOverride
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> false
#endif

    member x.ImplementedSlotSignatures =
        match x with
        | FSMeth(_, _, vref, _) -> vref.ImplementedSlotSignatures
        | MethInfoWithModifiedReturnType(mi, _) -> mi.ImplementedSlotSignatures
        | _ -> failwith "not supported"

    /// Indicates if this is an extension member.
    member x.IsExtensionMember =
        match x with
        | FSMeth (_, _, vref, pri) -> pri.IsSome || vref.IsExtensionMember
        | ILMeth (_, _, Some _) -> true
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsExtensionMember
        | _ -> false

    /// Indicates if this is an extension member (e.g. on a struct) that takes a byref arg
    member x.ObjArgNeedsAddress (amap: ImportMap, m) =
        (x.IsStruct && not x.IsExtensionMember) ||
        match x.GetObjArgTypes (amap, m, x.FormalMethodInst) with
        | [h] -> isByrefTy amap.g h
        | _ -> false

    /// Indicates if this is an F# extension member.
    member x.IsFSharpStyleExtensionMember =
        match x with 
        | FSMeth (_, _, vref, _) -> vref.IsExtensionMember 
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsFSharpStyleExtensionMember
        | _ -> false

    /// Indicates if this is an C#-style extension member.
    member x.IsCSharpStyleExtensionMember =
        match x with
        | FSMeth (_, _, vref, Some _) -> not vref.IsExtensionMember
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsCSharpStyleExtensionMember
        | ILMeth (_, _, Some _) -> true
        | _ -> false

    /// Add the actual type instantiation of the apparent type of an F# extension method.
    //
    // When an explicit type instantiation is given for an F# extension members the type
    // arguments implied by the object type are not given in source code. This means we must
    // add them explicitly. For example
    //    type List<'T> with
    //        member xs.Map<'U>(f : 'T -> 'U) = ....
    // is called as
    //    xs.Map<int>
    // but is compiled as a generic methods with two type arguments
    //     Map<'T, 'U>(this: List<'T>, f : 'T -> 'U)
    member x.AdjustUserTypeInstForFSharpStyleIndexedExtensionMembers tyargs =
        (if x.IsFSharpStyleExtensionMember then argsOfAppTy x.TcGlobals x.ApparentEnclosingAppType else []) @ tyargs

    /// Indicates if this method is a generated method associated with an F# CLIEvent property compiled as a .NET event
    member x.IsFSharpEventPropertyMethod =
        match x with
        | FSMeth(g, _, vref, _)  -> vref.IsFSharpEventProperty g
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsFSharpEventPropertyMethod
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> false
#endif
        | _ -> false

    /// Indicates if this method takes no arguments
    member x.IsNullary = (x.NumArgs = [0])

    /// Indicates if the enclosing type for the method is a value type.
    ///
    /// For an extension method, this indicates if the method extends a struct type.
    member x.IsStruct =
        isStructTy x.TcGlobals x.ApparentEnclosingType

    member x.IsOnReadOnlyType = 
        let g = x.TcGlobals
        let typeInfo = ILTypeInfo.FromType g x.ApparentEnclosingType
        typeInfo.IsReadOnly g

    /// Indicates if this method is read-only; usually by the [<IsReadOnly>] attribute on method or struct level.
    /// Must be an instance method.
    /// Receiver must be a struct type.
    member x.IsReadOnly =
        // Perf Review: Is there a way we can cache this result?        

        x.IsInstance &&
        x.IsStruct &&
        match x with
        | ILMeth (g, ilMethInfo, _) -> 
             ilMethInfo.IsReadOnly g || x.IsOnReadOnlyType
        | FSMeth _ -> false // F# defined methods not supported yet. Must be a language feature.
        | MethInfoWithModifiedReturnType(mi, _) -> mi.IsReadOnly
        | _ -> false

    /// Indicates, whether this method has `IsExternalInit` modreq.
    member x.HasExternalInit =
        match x with
        | ILMeth (_, ilMethInfo, _) -> HasExternalInit ilMethInfo.ILMethodRef
        | MethInfoWithModifiedReturnType(mi, _) -> mi.HasExternalInit
        | _ -> false

    /// Indicates if this method is an extension member that is read-only.
    /// An extension member is considered read-only if the first argument is a read-only byref (inref) type.
    member x.IsReadOnlyExtensionMember (amap: ImportMap, m) =
        x.IsExtensionMember && 
        x.TryObjArgByrefType(amap, m, x.FormalMethodInst)
        |> Option.exists (isInByrefTy amap.g)

    /// Build IL method infos.
    static member CreateILMeth (amap: ImportMap, m, ty: TType, md: ILMethodDef) =
        let tinfo = ILTypeInfo.FromType amap.g ty
        let nullableFallback = FromMethodAndClass(AttributesFromIL(md.MetadataIndex,md.CustomAttrsStored),tinfo.NullableAttributes)
        let mtps = ImportILGenericParameters (fun () -> amap) m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata nullableFallback md.GenericParams
        ILMeth (amap.g, ILMethInfo(amap.g, IlType tinfo, md, mtps), None)

    /// Build IL method infos for a C#-style extension method
    static member CreateILExtensionMeth (amap:ImportMap, m, apparentTy: TType, declaringTyconRef: TyconRef, extMethPri, md: ILMethodDef) =
        let scoref =  declaringTyconRef.CompiledRepresentationForNamedType.Scope
        let typeInfo = CSharpStyleExtension(declaringTyconRef,apparentTy)
        let declaringMetadata = declaringTyconRef.ILTyconRawMetadata
        let declaringAttributes = AttributesFromIL(declaringMetadata.MetadataIndex,declaringMetadata.CustomAttrsStored)
        let nullableFallback = FromMethodAndClass(AttributesFromIL(md.MetadataIndex,md.CustomAttrsStored),declaringAttributes)
        let mtps = ImportILGenericParameters (fun () -> amap) m scoref [] nullableFallback md.GenericParams
        ILMeth (amap.g, ILMethInfo(amap.g, typeInfo, md, mtps), extMethPri)

    /// Tests whether two method infos have the same underlying definition.
    /// Used to merge operator overloads collected from left and right of an operator constraint.
    /// Must be compatible with ItemsAreEffectivelyEqual relation.
    static member MethInfosUseIdenticalDefinitions x1 x2 =
        match x1, x2 with
        | ILMeth(_, x1, _), ILMeth(_, x2, _) -> (x1.RawMetadata ===  x2.RawMetadata)
        | FSMeth(g, _, vref1, _), FSMeth(_, _, vref2, _)  -> valRefEq g vref1 vref2
        | mi1, MethInfoWithModifiedReturnType(mi2, _)
        | MethInfoWithModifiedReturnType(mi1, _), mi2 -> MethInfo.MethInfosUseIdenticalDefinitions mi1 mi2
        | DefaultStructCtor _, DefaultStructCtor _ -> tyconRefEq x1.TcGlobals x1.DeclaringTyconRef x2.DeclaringTyconRef
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi1, _, _), ProvidedMeth(_, mi2, _, _)  -> ProvidedMethodBase.TaintedEquals (mi1, mi2)
#endif
        | _ -> false

    /// Calculates a hash code of method info. Must be compatible with ItemsAreEffectivelyEqual relation.
    member x.ComputeHashCode() =
        match x with
        | ILMeth(_, x1, _) -> hash x1.RawMetadata.Name
        | FSMeth(_, _, vref, _) -> hash vref.LogicalName
        | MethInfoWithModifiedReturnType(mi,_) -> mi.ComputeHashCode()
        | DefaultStructCtor(_, _ty) -> 34892 // "ty" doesn't support hashing. We could use "hash (tcrefOfAppTy g ty).CompiledName" or
                                           // something but we don't have a "g" parameter here yet. But this hash need only be very approximate anyway
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(_, mi, _, _) -> ProvidedMethodInfo.TaintedGetHashCode mi
#endif

    /// Apply a type instantiation to a method info, i.e. apply the instantiation to the enclosing type.
    member x.Instantiate(amap, m, inst) =
        match x with
        | ILMeth(_g, ilminfo, pri) ->
            match ilminfo with
            | ILMethInfo(_, IlType ty, md, _) -> MethInfo.CreateILMeth(amap, m, instType inst ty.ToType, md)
            | ILMethInfo(_, CSharpStyleExtension(declaringTyconRef,ty), md, _) -> MethInfo.CreateILExtensionMeth(amap, m, instType inst ty, declaringTyconRef, pri, md)
        | FSMeth(g, ty, vref, pri) -> FSMeth(g, instType inst ty, vref, pri)
        | MethInfoWithModifiedReturnType(mi, retTy) -> MethInfoWithModifiedReturnType(mi.Instantiate(amap, m, inst), retTy)
        | DefaultStructCtor(g, ty) -> DefaultStructCtor(g, instType inst ty)
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ ->
            match inst with
            | [] -> x
            | _ -> assert false; failwith "Not supported"
#endif

    /// Get the return type of a method info, where 'void' is returned as 'None'
    member x.GetCompiledReturnType (amap, m, minst) =
        match x with
        | ILMeth(_g, ilminfo, _) ->
            ilminfo.GetCompiledReturnType(amap, m, minst)
        | FSMeth(g, _, vref, _) ->
            let ty = x.ApparentEnclosingAppType
            let inst = GetInstantiationForMemberVal g x.IsCSharpStyleExtensionMember (ty, vref, minst)
            let _, _, retTy, _ = AnalyzeTypeOfMemberVal x.IsCSharpStyleExtensionMember g (ty, vref)
            retTy |> Option.map (instType inst)
        | MethInfoWithModifiedReturnType(_,retTy) -> Some retTy
        | DefaultStructCtor _ -> None
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, mi, _, m) ->
            GetCompiledReturnTyOfProvidedMethodInfo amap m mi
#endif

    /// Get the return type of a method info, where 'void' is returned as 'unit'
    member x.GetFSharpReturnType(amap, m, minst) =
        x.GetCompiledReturnType(amap, m, minst) |> GetFSharpViewOfReturnType amap.g

    member x.GetParamNames() =
        match x with
        | FSMeth (g, _, vref, _) ->
            ParamNameAndType.FromMember x.IsCSharpStyleExtensionMember g vref |> List.mapSquared (fun (ParamNameAndType (name, _)) -> name |> Option.map (fun x -> x.idText))
        | ILMeth (ilMethInfo = ilminfo) ->
            // A single group of tupled arguments
            [ ilminfo.ParamMetadata |> List.map (fun x -> x.Name) ]
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetParamNames()
#if !NO_TYPEPROVIDERS
        | ProvidedMeth (_, mi, _, m) ->
            // A single group of tupled arguments
            [ [ for p in mi.PApplyArray((fun mi -> mi.GetParameters()), "GetParameters", m) do
                yield p.PUntaint((fun p -> Some p.Name), m) ] ]
#endif
        | _ -> []

    /// Get the parameter types of a method info
    member x.GetParamTypes(amap, m, minst) =
        match x with
        | ILMeth(_g, ilminfo, _) ->
            // A single group of tupled arguments
            [ ilminfo.GetParamTypes(amap, m, minst) ]
        | FSMeth(g, ty, vref, _) ->
            let paramTypes = ParamNameAndType.FromMember x.IsCSharpStyleExtensionMember g vref
            let inst = GetInstantiationForMemberVal g x.IsCSharpStyleExtensionMember (ty, vref, minst)
            paramTypes |> List.mapSquared (fun (ParamNameAndType(_, ty)) -> instType inst ty)
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetParamTypes(amap,m,minst)
        | DefaultStructCtor _ -> []
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, mi, _, m) ->
            // A single group of tupled arguments
            [ [ for p in mi.PApplyArray((fun mi -> mi.GetParameters()), "GetParameters", m) do
                    yield ImportProvidedType amap m (p.PApply((fun p -> p.ParameterType), m)) ] ]
#endif

    /// Get the (zero or one) 'self'/'this'/'object' arguments associated with a method.
    /// An instance method returns one object argument.
    member x.GetObjArgTypes (amap, m, minst) =
        match x with
        | ILMeth(_, ilminfo, _) -> ilminfo.GetObjArgTypes(amap, m, minst)
        | FSMeth(g, _, vref, _) ->
            if x.IsInstance then
                let ty = x.ApparentEnclosingAppType
                // The 'this' pointer of an extension member can depend on the minst
                if x.IsExtensionMember then
                    let inst = GetInstantiationForMemberVal g x.IsCSharpStyleExtensionMember (ty, vref, minst)
                    let rawObjTy = GetObjTypeOfInstanceExtensionMethod g vref
                    [ rawObjTy |> instType inst ]
                else
                    [ ty ]
            else []
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetObjArgTypes(amap, m, minst)
        | DefaultStructCtor _ -> []
#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, mi, _, m) ->
            if x.IsInstance then [ ImportProvidedType amap m (mi.PApply((fun mi -> nonNull<ProvidedType> mi.DeclaringType), m)) ] // find the type of the 'this' argument
            else []
#endif

    /// Get custom attributes for method (only applicable for IL methods)
    member x.GetCustomAttrs() =
        match x with
        | ILMeth(_, ilMethInfo, _) -> ilMethInfo.RawMetadata.CustomAttrs
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetCustomAttrs()
        | _ -> ILAttributes.Empty

    /// Get the parameter attributes of a method info, which get combined with the parameter names and types
    member x.GetParamAttribs(amap, m) =
        match x with
        | ILMeth(g, ilMethInfo, _) ->
            [ [ for p in ilMethInfo.ParamMetadata do
                 let attrs = p.CustomAttrs
                 let isParamArrayArg = TryFindILAttribute g.attrib_ParamArrayAttribute attrs
                 let reflArgInfo =
                     match TryDecodeILAttribute g.attrib_ReflectedDefinitionAttribute.TypeRef attrs with
                     | Some ([ILAttribElem.Bool b ], _) ->  ReflectedArgInfo.Quote b
                     | Some _ -> ReflectedArgInfo.Quote false
                     | _ -> ReflectedArgInfo.None
                 let isOutArg = (p.IsOut && not p.IsIn)
                 let isInArg = (p.IsIn && not p.IsOut)
                 // Note: we get default argument values from VB and other .NET language metadata
                 let optArgInfo =  OptionalArgInfo.FromILParameter g amap m ilMethInfo.MetadataScope ilMethInfo.DeclaringTypeInst p

                 let isCallerLineNumberArg = TryFindILAttribute g.attrib_CallerLineNumberAttribute attrs
                 let isCallerFilePathArg = TryFindILAttribute g.attrib_CallerFilePathAttribute attrs
                 let isCallerMemberNameArg = TryFindILAttribute g.attrib_CallerMemberNameAttribute attrs

                 let callerInfo =
                    match isCallerLineNumberArg, isCallerFilePathArg, isCallerMemberNameArg with
                    | false, false, false -> NoCallerInfo
                    | true, false, false -> CallerLineNumber
                    | false, true, false -> CallerFilePath
                    | false, false, true -> CallerMemberName
                    | _, _, _ ->
                        // if multiple caller info attributes are specified, pick the "wrong" one here
                        // so that we get an error later
                        if p.Type.TypeRef.FullName = "System.Int32" then CallerFilePath
                        else CallerLineNumber

                 ParamAttribs(isParamArrayArg, isInArg, isOutArg, optArgInfo, callerInfo, reflArgInfo) ] ]

        | FSMeth(g, _, vref, _) ->
            GetArgInfosOfMember x.IsCSharpStyleExtensionMember g vref
            |> List.mapSquared (CrackParamAttribsInfo g)
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetParamAttribs(amap, m)
        | DefaultStructCtor _ ->
            [[]]

#if !NO_TYPEPROVIDERS
        | ProvidedMeth(amap, mi, _, _) ->
            // A single group of tupled arguments
            [ [for p in mi.PApplyArray((fun mi -> mi.GetParameters()), "GetParameters", m) do
                let isParamArrayArg = p.PUntaint((fun px -> (px :> IProvidedCustomAttributeProvider).GetAttributeConstructorArgs(p.TypeProvider.PUntaintNoFailure id, !! typeof<ParamArrayAttribute>.FullName).IsSome), m)
                let optArgInfo =  OptionalArgInfoOfProvidedParameter amap m p
                let reflArgInfo =
                    match p.PUntaint((fun px -> (px :> IProvidedCustomAttributeProvider).GetAttributeConstructorArgs(p.TypeProvider.PUntaintNoFailure id, !! typeof<ReflectedDefinitionAttribute>.FullName)), m) with
                    | Some ([ Some (:? bool as b) ], _) -> ReflectedArgInfo.Quote b
                    | Some _ -> ReflectedArgInfo.Quote false
                    | None -> ReflectedArgInfo.None
                let isOutArg = p.PUntaint((fun p -> p.IsOut && not p.IsIn), m)
                let isInArg = p.PUntaint((fun p -> p.IsIn && not p.IsOut), m)
                ParamAttribs(isParamArrayArg, isInArg, isOutArg, optArgInfo, NoCallerInfo, reflArgInfo)] ]
#endif

    /// Get the signature of an abstract method slot.
    //
    // This code has grown organically over time. We've managed to unify the ILMeth+ProvidedMeth paths.
    // The FSMeth, ILMeth+ProvidedMeth paths can probably be unified too.
    member x.GetSlotSig(amap, m) =
        match x with
        | FSMeth(g, _, vref, _) ->
            match vref.RecursiveValInfo with
            | ValInRecScope false -> error(Error((FSComp.SR.InvalidRecursiveReferenceToAbstractSlot()), m))
            | _ -> ()

            let allTyparsFromMethod, _, _, retTy, _ = GetTypeOfMemberInMemberForm g vref

            // A slot signature is w.r.t. the type variables of the type it is associated with.
            // So we have to rename from the member type variables to the type variables of the type.
            let formalEnclosingTypars = x.ApparentEnclosingTyconRef.Typars m
            let formalEnclosingTyparsFromMethod, formalMethTypars = List.splitAt formalEnclosingTypars.Length allTyparsFromMethod
            let methodToParentRenaming, _ = mkTyparToTyparRenaming formalEnclosingTyparsFromMethod formalEnclosingTypars
            let formalParams =
                GetArgInfosOfMember x.IsCSharpStyleExtensionMember g vref
                |> List.mapSquared (map1Of2 (instType methodToParentRenaming) >> MakeSlotParam )
            let formalRetTy = Option.map (instType methodToParentRenaming) retTy
            MakeSlotSig(x.LogicalName, x.ApparentEnclosingType, formalEnclosingTypars, formalMethTypars, formalParams, formalRetTy)
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetSlotSig(amap, m)
        | DefaultStructCtor _ -> error(InternalError("no slotsig for DefaultStructCtor", m))
        | _ ->
            let g = x.TcGlobals
            // slotsigs must contain the formal types for the arguments and return type
            // a _formal_ 'void' return type is represented as a 'unit' type.
            // slotsigs are independent of instantiation: if an instantiation
            // happens to make the return type 'unit' (i.e. it was originally a variable type
            // then that does not correspond to a slotsig compiled as a 'void' return type.
            // REVIEW: should we copy down attributes to slot params?
            let tcref =  tcrefOfAppTy g x.ApparentEnclosingAppType
            let formalEnclosingTyparsOrig = tcref.Typars m
            let formalEnclosingTypars = copyTypars false formalEnclosingTyparsOrig
            let _, formalEnclosingTyparTys = FixupNewTypars m [] [] formalEnclosingTyparsOrig formalEnclosingTypars
            let formalMethTypars = copyTypars false x.FormalMethodTypars
            let _, formalMethTyparTys = FixupNewTypars m formalEnclosingTypars formalEnclosingTyparTys x.FormalMethodTypars formalMethTypars

            let formalRetTy, formalParams =
                match x with
                | ILMeth(_, ilminfo, _) ->                
                    let ftinfo = ILTypeInfo.FromType g (TType_app(tcref, formalEnclosingTyparTys, g.knownWithoutNull))

                    let ilReturn = ilminfo.RawMetadata.Return
                    let nullableSource = {DirectAttributes = AttributesFromIL(ilReturn.MetadataIndex,ilReturn.CustomAttrsStored); Fallback = ilminfo.NullableFallback}

                    let formalRetTy = ImportReturnTypeFromMetadata amap m nullableSource ilReturn.Type ftinfo.ILScopeRef ftinfo.TypeInstOfRawMetadata formalMethTyparTys
                    let formalParams =
                        [ [ for p in ilminfo.RawMetadata.Parameters do
                                let nullableSource = {nullableSource with DirectAttributes = AttributesFromIL(p.MetadataIndex,p.CustomAttrsStored)}
                                let paramTy = ImportILTypeFromMetadataWithAttributes amap m ftinfo.ILScopeRef ftinfo.TypeInstOfRawMetadata formalMethTyparTys nullableSource p.Type
                                yield TSlotParam(p.Name, paramTy, p.IsIn, p.IsOut, p.IsOptional, []) ] ]
                    formalRetTy, formalParams
#if !NO_TYPEPROVIDERS
                | ProvidedMeth (_, mi, _, _) ->
                    // GENERIC TYPE PROVIDERS: for generics, formal types should be  generated here, not the actual types
                    // For non-generic type providers there is no difference
                    let formalRetTy = x.GetCompiledReturnType(amap, m, formalMethTyparTys)
                    // GENERIC TYPE PROVIDERS: formal types should be  generated here, not the actual types
                    // For non-generic type providers there is no difference
                    let formalParams =
                        [ [ for p in mi.PApplyArray((fun mi -> mi.GetParameters()), "GetParameters", m) do
                                let paramName = p.PUntaint((fun p -> match p.Name with "" -> None | s -> Some s), m)
                                let paramTy = ImportProvidedType amap m (p.PApply((fun p -> p.ParameterType), m))
                                let isIn, isOut, isOptional = p.PUntaint((fun p -> p.IsIn, p.IsOut, p.IsOptional), m)
                                yield TSlotParam(paramName, paramTy, isIn, isOut, isOptional, []) ] ]
                    formalRetTy, formalParams
#endif
                | _ -> failwith "unreachable"

            MakeSlotSig(x.LogicalName, x.ApparentEnclosingType, formalEnclosingTypars, formalMethTypars, formalParams, formalRetTy)

    /// Get the ParamData objects for the parameters of a MethInfo
    member x.GetParamDatas(amap, m, minst) =
        match x with
        | MethInfoWithModifiedReturnType(mi,_) -> mi.GetParamDatas(amap, m, minst)
        | _ ->
            let paramNamesAndTypes =
                match x with
                | ILMeth(_g, ilminfo, _) ->
                    [ ilminfo.GetParamNamesAndTypes(amap, m, minst)  ]
                | FSMeth(g, _, vref, _) ->
                    let ty = x.ApparentEnclosingAppType
                    let items = ParamNameAndType.FromMember x.IsCSharpStyleExtensionMember g vref
                    let inst = GetInstantiationForMemberVal g x.IsCSharpStyleExtensionMember (ty, vref, minst)
                    items |> ParamNameAndType.InstantiateCurried inst
                | MethInfoWithModifiedReturnType(_mi,_) -> failwith "unreachable"
                | DefaultStructCtor _ ->
                    [[]]
#if !NO_TYPEPROVIDERS
                | ProvidedMeth(amap, mi, _, _) ->
                    // A single set of tupled parameters
                    [ [for p in mi.PApplyArray((fun mi -> mi.GetParameters()), "GetParameters", m) do
                            let paramName =
                                match p.PUntaint((fun p -> p.Name), m) with
                                | "" -> None
                                | name -> Some (mkSynId m name)
                            let paramTy =
                                match p.PApply((fun p -> p.ParameterType), m) with
                                | Tainted.Null ->  amap.g.unit_ty
                                | Tainted.NonNull parameterType -> ImportProvidedType amap m parameterType
                            yield ParamNameAndType(paramName, paramTy) ] ]

#endif

            let paramAttribs = x.GetParamAttribs(amap, m)
            (paramAttribs, paramNamesAndTypes) ||> List.map2 (List.map2 (fun info (ParamNameAndType(nmOpt, pty)) ->
                 let (ParamAttribs(isParamArrayArg, isInArg, isOutArg, optArgInfo, callerInfo, reflArgInfo)) = info
                 ParamData(isParamArrayArg, isInArg, isOutArg, optArgInfo, callerInfo, nmOpt, reflArgInfo, pty)))

    member x.HasGenericRetTy() =
        match x with
        | ILMeth(_g, ilminfo, _) -> ilminfo.RawMetadata.Return.Type.IsTypeVar
        | FSMeth(g, _, vref, _) ->
            let _, _, _, retTy, _ = GetTypeOfMemberInMemberForm g vref
            match retTy with
            | Some retTy -> isTyparTy g retTy
            | None -> false
        | MethInfoWithModifiedReturnType _ -> false
        | DefaultStructCtor _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedMeth _ -> false
#endif

    /// Get the ParamData objects for the parameters of a MethInfo
    member x.HasParamArrayArg(amap, m, minst) =
        x.GetParamDatas(amap, m, minst) |> List.existsSquared (fun (ParamData(isParamArrayArg, _, _, _, _, _, _, _)) -> isParamArrayArg)

    /// Select all the type parameters of the declaring type of a method.
    ///
    /// For extension methods, no type parameters are returned, because all the
    /// type parameters are part of the apparent type, rather the
    /// declaring type, even for extension methods extending generic types.
    member x.GetFormalTyparsOfDeclaringType m =
        if x.IsExtensionMember then []
        else
            match x with
            | FSMeth(g, _, vref, _) ->
                let ty = x.ApparentEnclosingAppType
                let memberParentTypars, _, _, _ = AnalyzeTypeOfMemberVal false g (ty, vref)
                memberParentTypars
            | _ ->
                x.DeclaringTyconRef.Typars m

    /// Tries to get the object arg type if it's a byref type.
    member x.TryObjArgByrefType(amap, m, minst) =
        x.GetObjArgTypes(amap, m, minst)
        |> List.tryHead
        |> Option.bind (fun ty ->
            if isByrefTy x.TcGlobals ty then Some ty
            else None)

/// Represents a single use of a IL or provided field from one point in an F# program
[<NoComparison; NoEquality>]
type ILFieldInfo =
     /// Represents a single use of a field backed by Abstract IL metadata
    | ILFieldInfo of ilTypeInfo: ILTypeInfo * ilFieldDef: ILFieldDef
#if !NO_TYPEPROVIDERS
     /// Represents a single use of a field backed by provided metadata
    | ProvidedField of amap: ImportMap * providedField: Tainted<ProvidedFieldInfo> * range: range
#endif

    /// Get the enclosing ("parent"/"declaring") type of the field.
    member x.ApparentEnclosingType =
        match x with
        | ILFieldInfo(tinfo, _) -> tinfo.ToType
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, fi, m) -> (ImportProvidedType amap m (fi.PApply((fun fi -> nonNull<ProvidedType> fi.DeclaringType), m)))
#endif

    member x.ApparentEnclosingAppType = x.ApparentEnclosingType

    member x.ApparentEnclosingTyconRef = tcrefOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    member x.DeclaringTyconRef = x.ApparentEnclosingTyconRef

    member x.TcGlobals =
        match x with
        | ILFieldInfo(tinfo, _) -> tinfo.TcGlobals
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, _, _) -> amap.g
#endif

     /// Get a reference to the declaring type of the field as an ILTypeRef
    member x.ILTypeRef =
        match x with
        | ILFieldInfo(tinfo, _) -> tinfo.ILTypeRef
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, fi, m) -> (ImportProvidedTypeAsILType amap m (fi.PApply((fun fi -> nonNull<ProvidedType> fi.DeclaringType), m))).TypeRef
#endif

     /// Get the scope used to interpret IL metadata
    member x.ScopeRef = x.ILTypeRef.Scope

    /// Get the type instantiation of the declaring type of the field
    member x.TypeInst =
        match x with
        | ILFieldInfo(tinfo, _) -> tinfo.TypeInstOfRawMetadata
#if !NO_TYPEPROVIDERS
        | ProvidedField _ -> [] /// GENERIC TYPE PROVIDERS
#endif

     /// Get the name of the field
    member x.FieldName =
        match x with
        | ILFieldInfo(_, pd) -> pd.Name
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi, m) -> fi.PUntaint((fun fi -> fi.Name), m)
#endif

    member x.DisplayNameCore = x.FieldName

     /// Indicates if the field is readonly (in the .NET/C# sense of readonly)
    member x.IsInitOnly =
        match x with
        | ILFieldInfo(_, pd) -> pd.IsInitOnly
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi, m) -> fi.PUntaint((fun fi -> fi.IsInitOnly), m)
#endif

    /// Indicates if the field is a member of a struct or enum type
    member x.IsValueType =
        match x with
        | ILFieldInfo(tinfo, _) -> tinfo.IsValueType
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, _, _) -> isStructTy amap.g x.ApparentEnclosingType
#endif

     /// Indicates if the field is static
    member x.IsStatic =
        match x with
        | ILFieldInfo(_, pd) -> pd.IsStatic
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi, m) -> fi.PUntaint((fun fi -> fi.IsStatic), m)
#endif

     /// Indicates if the field has the 'specialname' property in the .NET IL
    member x.IsSpecialName =
        match x with
        | ILFieldInfo(_, pd) -> pd.IsSpecialName
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi, m) -> fi.PUntaint((fun fi -> fi.IsSpecialName), m)
#endif

     /// Indicates if the field is a literal field with an associated literal value
    member x.LiteralValue =
        match x with
        | ILFieldInfo(_, pd) -> if pd.IsLiteral then pd.LiteralValue else None
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi, m) ->
            if fi.PUntaint((fun fi -> fi.IsLiteral), m) then
                Some (ILFieldInit.FromProvidedObj m (fi.PUntaint((fun fi -> fi.GetRawConstantValue()), m)))
            else
                None
#endif

     /// Get the type of the field as an IL type
    member x.ILFieldType =
        match x with
        | ILFieldInfo (_, fdef) -> fdef.FieldType
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, fi, m) -> ImportProvidedTypeAsILType amap m (fi.PApply((fun fi -> fi.FieldType), m))
#endif

     /// Get the type of the field as an F# type
    member x.FieldType(amap, m) =
        match x with     
        | ILFieldInfo (tinfo, fdef) -> 
            let nullness = {DirectAttributes = AttributesFromIL(fdef.MetadataIndex,fdef.CustomAttrsStored); Fallback = tinfo.NullableClassSource}
            ImportILTypeFromMetadata amap m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata [] nullness fdef.FieldType
#if !NO_TYPEPROVIDERS
        | ProvidedField(amap, fi, m) -> ImportProvidedType amap m (fi.PApply((fun fi -> fi.FieldType), m))
#endif

    /// Tests whether two infos have the same underlying definition.
    /// Must be compatible with ItemsAreEffectivelyEqual relation.
    static member ILFieldInfosUseIdenticalDefinitions x1 x2 =
        match x1, x2 with
        | ILFieldInfo(_, x1), ILFieldInfo(_, x2) -> (x1 === x2)
#if !NO_TYPEPROVIDERS
        | ProvidedField(_, fi1, _), ProvidedField(_, fi2, _)-> ProvidedFieldInfo.TaintedEquals (fi1, fi2)
        | _ -> false
#endif
     /// Get an (uninstantiated) reference to the field as an Abstract IL ILFieldRef
    member x.ILFieldRef = rescopeILFieldRef x.ScopeRef (mkILFieldRef(x.ILTypeRef, x.FieldName, x.ILFieldType))

    /// Calculates a hash code of field info. Must be compatible with ItemsAreEffectivelyEqual relation.
    member x.ComputeHashCode() = hash x.FieldName

    override x.ToString() =  x.FieldName


/// Describes an F# use of a field in an F#-declared record, class or struct type
[<NoComparison; NoEquality>]
type RecdFieldInfo =
    | RecdFieldInfo of typeInst: TypeInst * recdFieldRef: RecdFieldRef

    /// Get the generic instantiation of the declaring type of the field
    member x.TypeInst = let (RecdFieldInfo(tinst, _)) = x in tinst

    /// Get a reference to the F# metadata for the uninstantiated field
    member x.RecdFieldRef = let (RecdFieldInfo(_, rfref)) = x in rfref

    /// Get the F# metadata for the uninstantiated field
    member x.RecdField = x.RecdFieldRef.RecdField

    /// Indicate if the field is a static field in an F#-declared record, class or struct type
    member x.IsStatic = x.RecdField.IsStatic

    /// Indicate if the field is a literal field in an F#-declared record, class or struct type
    member x.LiteralValue = x.RecdField.LiteralValue

    /// Get a reference to the F# metadata for the F#-declared record, class or struct type
    member x.TyconRef = x.RecdFieldRef.TyconRef

    /// Get the F# metadata for the F#-declared record, class or struct type
    member x.Tycon = x.RecdFieldRef.Tycon

    /// Get the logical name of the field in an F#-declared record, class or struct type
    member x.LogicalName = x.RecdField.LogicalName

    member x.DisplayNameCore = x.RecdField.DisplayNameCore

    member x.DisplayName = x.RecdField.DisplayName

    /// Get the (instantiated) type of the field in an F#-declared record, class or struct type
    member x.FieldType = actualTyOfRecdFieldRef x.RecdFieldRef x.TypeInst

    /// Get the enclosing (declaring) type of the field in an F#-declared record, class or struct type
    member x.DeclaringType = TType_app (x.RecdFieldRef.TyconRef, x.TypeInst, KnownWithoutNull) // TODO NULLNESS - qualify this 

    override x.ToString() = x.TyconRef.ToString() + "::" + x.LogicalName

/// Describes an F# use of a union case
[<NoComparison; NoEquality>]
type UnionCaseInfo =
    | UnionCaseInfo of typeInst: TypeInst * unionCaseRef: UnionCaseRef

    /// Get the list of types for the instantiation of the type parameters of the declaring type of the union case
    member x.TypeInst = let (UnionCaseInfo(tinst, _)) = x in tinst

    /// Get a reference to the F# metadata for the uninstantiated union case
    member x.UnionCaseRef = let (UnionCaseInfo(_, ucref)) = x in ucref

    /// Get the F# metadata for the uninstantiated union case
    member x.UnionCase = x.UnionCaseRef.UnionCase

    /// Get a reference to the F# metadata for the declaring union type
    member x.TyconRef = x.UnionCaseRef.TyconRef

    /// Get the F# metadata for the declaring union type
    member x.Tycon = x.UnionCaseRef.Tycon

    /// Get the logical name of the union case. 
    member x.LogicalName = x.UnionCase.LogicalName

    /// Get the core of the display name of the union case
    ///
    /// Backticks and parens are not added for non-identifiers.
    ///
    /// Note logical names op_Nil and op_ColonColon become [] and :: respectively.
    member x.DisplayNameCore = x.UnionCase.DisplayNameCore

    /// Get the display name of the union case
    ///
    /// Backticks and parens are added implicitly for non-identifiers.
    ///
    /// Note logical names op_Nil and op_ColonColon become ([]) and (::) respectively.
    member x.DisplayName = x.UnionCase.DisplayName

    /// Get the instantiation of the type parameters of the declaring type of the union case
    member x.GetTyparInst m =  mkTyparInst (x.TyconRef.Typars m) x.TypeInst

    override x.ToString() = x.TyconRef.ToString() + "::" + x.DisplayNameCore

/// Describes an F# use of a property backed by Abstract IL metadata
[<NoComparison; NoEquality>]
type ILPropInfo =
    | ILPropInfo of ilTypeInfo: ILTypeInfo * ilPropertyDef: ILPropertyDef

    /// Get the TcGlobals governing this value
    member x.TcGlobals = match x with ILPropInfo(tinfo, _) -> tinfo.TcGlobals

    /// Get the declaring IL type of the IL property, including any generic instantiation
    member x.ILTypeInfo = match x with ILPropInfo(tinfo, _) -> tinfo

    /// Get the apparent declaring type of the method as an F# type.
    /// If this is a C#-style extension method then this is the type which the method
    /// appears to extend. This may be a variable type.
    member x.ApparentEnclosingType = match x with ILPropInfo(tinfo, _) -> tinfo.ToType

    /// Like ApparentEnclosingType but use the compiled nominal type if this is a method on a tuple type
    member x.ApparentEnclosingAppType = convertToTypeWithMetadataIfPossible x.TcGlobals x.ApparentEnclosingType

    /// Get the raw Abstract IL metadata for the IL property
    member x.RawMetadata = match x with ILPropInfo(_, pd) -> pd

    /// Get the name of the IL property
    member x.PropertyName = x.RawMetadata.Name

    /// Gets the ILMethInfo of the 'get' method for the IL property
    member x.GetterMethod =
        assert x.HasGetter
        let mdef = resolveILMethodRef x.ILTypeInfo.RawMetadata x.RawMetadata.GetMethod.Value
        ILMethInfo(x.TcGlobals, IlType x.ILTypeInfo, mdef, [])

    /// Gets the ILMethInfo of the 'set' method for the IL property
    member x.SetterMethod =
        assert x.HasSetter
        let mdef = resolveILMethodRef x.ILTypeInfo.RawMetadata x.RawMetadata.SetMethod.Value
        ILMethInfo(x.TcGlobals, IlType x.ILTypeInfo, mdef, [])

    /// Indicates if the IL property has a 'get' method
    member x.HasGetter = Option.isSome x.RawMetadata.GetMethod

    /// Indicates if the IL property has a 'set' method
    member x.HasSetter = Option.isSome x.RawMetadata.SetMethod

    /// Indicates whether IL property has an init-only setter (i.e. has the `System.Runtime.CompilerServices.IsExternalInit` modifier)
    member x.IsSetterInitOnly =
        x.HasSetter && HasExternalInit x.SetterMethod.ILMethodRef

    /// Indicates if the IL property is static
    member x.IsStatic = (x.RawMetadata.CallingConv = ILThisConvention.Static)

    /// Indicates if the IL property is virtual
    member x.IsVirtual =
        (x.HasGetter && x.GetterMethod.IsVirtual) ||
        (x.HasSetter && x.SetterMethod.IsVirtual)

    /// Indicates if the IL property is logically a 'newslot', i.e. hides any previous slots of the same name.
    member x.IsNewSlot =
        (x.HasGetter && x.GetterMethod.IsNewSlot) ||
        (x.HasSetter && x.SetterMethod.IsNewSlot)

    /// Indicates if the property is required, i.e. has RequiredMemberAttribute applied.
    member x.IsRequired = TryFindILAttribute x.TcGlobals.attrib_RequiredMemberAttribute x.RawMetadata.CustomAttrs

    /// Get the names and types of the indexer arguments associated with the IL property.
    ///
    /// Any type parameters of the enclosing type are instantiated in the type returned.
    member x.GetParamNamesAndTypes(amap, m) =
        let (ILPropInfo (tinfo, pdef)) = x
        if x.HasGetter then
            x.GetterMethod.GetParamNamesAndTypes(amap,m,tinfo.TypeInstOfRawMetadata)
        else if x.HasSetter then
            x.SetterMethod.GetParamNamesAndTypes(amap,m,tinfo.TypeInstOfRawMetadata)
        else
            // Fallback-only for invalid properties
            pdef.Args |> List.map (fun ty -> ParamNameAndType(None, ImportILTypeFromMetadataSkipNullness amap m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata [] ty) )

    /// Get the types of the indexer arguments associated with the IL property.
    ///
    /// Any type parameters of the enclosing type are instantiated in the type returned.
    member x.GetParamTypes(amap, m) =
        let (ILPropInfo (tinfo, pdef)) = x
        if x.HasGetter then
            x.GetterMethod.GetParamTypes(amap,m,tinfo.TypeInstOfRawMetadata)
        else if x.HasSetter then
            x.SetterMethod.GetParamTypes(amap,m,tinfo.TypeInstOfRawMetadata)
        else
            // Fallback-only for invalid properties
            pdef.Args |> List.map (fun ty -> ImportILTypeFromMetadataSkipNullness amap m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata [] ty)

    /// Get the return type of the IL property.
    ///
    /// Any type parameters of the enclosing type are instantiated in the type returned.
    member x.GetPropertyType (amap, m) =
        let (ILPropInfo (tinfo, pdef)) = x
        let nullness = {DirectAttributes = AttributesFromIL(pdef.MetadataIndex,pdef.CustomAttrsStored); Fallback = tinfo.NullableClassSource}
        ImportILTypeFromMetadata amap m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata [] nullness pdef.PropertyType

    override x.ToString() = !!x.ILTypeInfo.ToString() + "::" + x.PropertyName

/// Describes an F# use of a property
[<NoComparison; NoEquality>]
type PropInfo =
    /// An F# use of a property backed by F#-declared metadata
    | FSProp of tcGlobals: TcGlobals * apparentEnclTy: TType * getter: ValRef option * setter: ValRef option

    /// An F# use of a property backed by Abstract IL metadata
    | ILProp of ilPropInfo: ILPropInfo

#if !NO_TYPEPROVIDERS
    /// An F# use of a property backed by provided metadata
    | ProvidedProp of amap: ImportMap * providedProp: Tainted<ProvidedPropertyInfo> * range: range
#endif

    /// Get the enclosing type of the property.
    ///
    /// If this is an extension member, then this is the apparent parent, i.e. the type the property appears to extend.
    member x.ApparentEnclosingType =
        match x with
        | ILProp ilpinfo -> ilpinfo.ILTypeInfo.ToType
        | FSProp(_, ty, _, _) -> ty
#if !NO_TYPEPROVIDERS
        | ProvidedProp(amap, pi, m) ->
            ImportProvidedType amap m (pi.PApply((fun pi -> nonNull<ProvidedType> pi.DeclaringType), m)) 
#endif

    /// Get the enclosing type of the method info, using a nominal type for tuple types
    member x.ApparentEnclosingAppType =
        match x with
        | ILProp ilpinfo -> ilpinfo.ApparentEnclosingAppType
        | _ -> x.ApparentEnclosingType

    member x.ApparentEnclosingTyconRef = tcrefOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    /// Get the declaring type or module holding the method.
    /// Note that C#-style extension properties don't exist in the C# design as yet.
    /// If this is an F#-style extension method it is the logical module
    /// holding the value for the extension method.
    member x.DeclaringTyconRef   =
        match x.ArbitraryValRef with
        | Some vref when x.IsExtensionMember && vref.HasDeclaringEntity -> vref.DeclaringEntity
        | _ -> x.ApparentEnclosingTyconRef

    /// Try to get an arbitrary F# ValRef associated with the member. This is to determine if the member is virtual, amongst other things.
    member x.ArbitraryValRef : ValRef option =
        match x with
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> Some vref
        | FSProp(_, _, None, None) -> failwith "unreachable"
        | _ -> None

    /// Indicates if this property has an associated XML comment authored in this assembly.
    member x.HasDirectXmlComment =
        match x with
        | FSProp(g, _, Some vref, _)
        | FSProp(g, _, _, Some vref) -> valRefInThisAssembly g.compilingFSharpCore vref
#if !NO_TYPEPROVIDERS
        | ProvidedProp _ -> true
#endif
        | _ -> false

    /// Get the logical name of the property.
    member x.PropertyName =
        match x with
        | ILProp ilpinfo -> ilpinfo.PropertyName
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.PropertyName
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) -> pi.PUntaint((fun pi -> pi.Name), m)
#endif
        | FSProp _ -> failwith "unreachable"

     /// Get the property name in DisplayName form
    member x.DisplayName =
        match x with
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.DisplayName
        | _ -> x.PropertyName |> PrettyNaming.ConvertValLogicalNameToDisplayName false

     /// Get the property name in DisplayNameCore form
    member x.DisplayNameCore =
        match x with
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.DisplayNameCore
        | _ -> x.PropertyName |> PrettyNaming.ConvertValLogicalNameToDisplayNameCore

    /// Indicates if this property has an associated getter method.
    member x.HasGetter =
        match x with
        | ILProp ilpinfo-> ilpinfo.HasGetter
        | FSProp(_, _, x, _) -> Option.isSome x
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) -> pi.PUntaint((fun pi -> pi.CanRead), m)
#endif

    /// Indicates if this property has an associated setter method.
    member x.HasSetter =
        match x with
        | ILProp ilpinfo -> ilpinfo.HasSetter
        | FSProp(_, _, _, x) -> Option.isSome x
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) -> pi.PUntaint((fun pi -> pi.CanWrite), m)
#endif

    member x.GetterAccessibility =
        match x with
        | ILProp ilpinfo when ilpinfo.HasGetter -> Some taccessPublic
        | ILProp _ -> None

        | FSProp(_, _, Some getter, _) -> Some getter.Accessibility
        | FSProp _ -> None

#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) -> pi.PUntaint((fun pi -> if pi.CanWrite then Some taccessPublic else None), m)
#endif

    member x.SetterAccessibility =
        match x with
        | ILProp ilpinfo when ilpinfo.HasSetter -> Some taccessPublic
        | ILProp _ -> None

        | FSProp(_, _, _, Some setter) -> Some setter.Accessibility
        | FSProp _ -> None

#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) -> pi.PUntaint((fun pi -> if pi.CanWrite then Some taccessPublic else None), m)
#endif

    member x.IsProtectedAccessibility =
        match x with
        | ILProp ilpinfo when ilpinfo.HasGetter && ilpinfo.HasSetter -> 
            struct(ilpinfo.GetterMethod.IsProtectedAccessibility, ilpinfo.SetterMethod.IsProtectedAccessibility)
        | ILProp ilpinfo when ilpinfo.HasGetter -> struct(ilpinfo.GetterMethod.IsProtectedAccessibility, false)
        | ILProp ilpinfo when ilpinfo.HasSetter -> struct(false, ilpinfo.SetterMethod.IsProtectedAccessibility)
        | _ -> struct(false, false)

    member x.IsSetterInitOnly =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsSetterInitOnly
        | FSProp _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedProp _ -> false
#endif

    member x.IsRequired =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsRequired
        | FSProp _ -> false
#if !NO_TYPEPROVIDERS
        | ProvidedProp _ -> false
#endif

    /// Indicates if this is an extension member
    member x.IsExtensionMember =
        match x.ArbitraryValRef with
        | Some vref -> vref.IsExtensionMember
        | _ -> false

    /// True if the getter (or, if absent, the setter) is a virtual method
    // REVIEW: for IL properties this is getter OR setter. For F# properties it is getter ELSE setter
    member x.IsVirtualProperty =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsVirtual
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.IsVirtualMember
        | FSProp _-> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            let mi = ArbitraryMethodInfoOfPropertyInfo pi m
            mi.PUntaint((fun mi -> mi.IsVirtual), m)
#endif

    /// Indicates if the property is logically a 'newslot', i.e. hides any previous slots of the same name.
    member x.IsNewSlot =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsNewSlot
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.IsDispatchSlotMember
        | FSProp(_, _, None, None) -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            let mi = ArbitraryMethodInfoOfPropertyInfo pi m
            mi.PUntaint((fun mi -> mi.IsHideBySig), m)
#endif

    /// Indicates if the getter (or, if absent, the setter) for the property is a dispatch slot.
    // REVIEW: for IL properties this is getter OR setter. For F# properties it is getter ELSE setter
    member x.IsDispatchSlot =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsVirtual
        | FSProp(g, ty, Some vref, _)
        | FSProp(g, ty, _, Some vref) ->
            isInterfaceTy g ty  || vref.MemberInfo.Value.MemberFlags.IsDispatchSlot
        | FSProp _ -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            let mi = ArbitraryMethodInfoOfPropertyInfo pi m
            mi.PUntaint((fun mi -> mi.IsVirtual), m)
#endif

    /// Indicates if this property is static.
    member x.IsStatic =
        match x with
        | ILProp ilpinfo -> ilpinfo.IsStatic
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> not vref.IsInstanceMember
        | FSProp(_, _, None, None) -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            (ArbitraryMethodInfoOfPropertyInfo pi m).PUntaint((fun mi -> mi.IsStatic), m)
#endif

    /// Indicates if this property is marked 'override' and thus definitely overrides another property.
    member x.IsDefiniteFSharpOverride =
        match x.ArbitraryValRef with
        | Some vref -> vref.IsDefiniteFSharpOverrideMember
        | None -> false

    member x.ImplementedSlotSignatures =
        x.ArbitraryValRef.Value.ImplementedSlotSignatures

    member x.IsFSharpExplicitInterfaceImplementation =
        match x.ArbitraryValRef with
        | Some vref -> vref.IsFSharpExplicitInterfaceImplementation x.TcGlobals
        | None -> false

    /// Indicates if this property is an indexer property, i.e. a property with arguments.
    /// <code lang="fsharp">
    /// member x.Prop with 
    ///     get (indexPiece1:int,indexPiece2: string) = ...
    ///     and set (indexPiece1:int,indexPiece2: string) value = ... 
    /// </code>
    member x.IsIndexer =
        match x with
        | ILProp(ILPropInfo(_, pdef)) -> pdef.Args.Length <> 0
        | FSProp(g, _, Some vref, _)  ->
            // A getter has signature  { OptionalObjectType } -> Unit -> PropertyType
            // A getter indexer has signature  { OptionalObjectType } -> TupledIndexerArguments -> PropertyType
            match ArgInfosOfMember g vref with
            | [argInfos] -> not (List.isEmpty argInfos)
            | _ -> false
        | FSProp(g, _, _, Some vref) ->
            // A setter has signature  { OptionalObjectType } -> PropertyType -> Void
            // A setter indexer has signature  { OptionalObjectType } -> TupledIndexerArguments -> PropertyType -> Void
            let arginfos = ArgInfosOfMember g vref
            arginfos.Length = 1 && arginfos.Head.Length >= 2
        | FSProp(_, _, None, None) ->
            failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            pi.PApplyArray((fun pi -> pi.GetIndexParameters()),"GetIndexParameters", m).Length>0
#endif

    /// Indicates if this is an F# property compiled as a CLI event, e.g. a [<CLIEvent>] property.
    member x.IsFSharpEventProperty =
        match x with
        | FSProp(g, _, Some vref, None)  -> vref.IsFSharpEventProperty g
#if !NO_TYPEPROVIDERS
        | ProvidedProp _ -> false
#endif
        | _ -> false

    /// Return a new property info where there is no associated setter, only an associated getter.
    ///
    /// Property infos can combine getters and setters, assuming they are consistent w.r.t. 'virtual', indexer argument types etc.
    /// When checking consistency we split these apart
    member x.DropSetter() =
        match x with
        | FSProp(g, ty, Some vref, _)  -> FSProp(g, ty, Some vref, None)
        | _ -> x

    /// Return a new property info where there is no associated getter, only an associated setter.
    member x.DropGetter() =
        match x with
        | FSProp(g, ty, _, Some vref)  -> FSProp(g, ty, None, Some vref)
        | _ -> x

    /// Get the intra-assembly XML documentation for the property.
    member x.XmlDoc =
        match x with
        | ILProp _ -> XmlDoc.Empty
        | FSProp(_, _, Some vref, _)
        | FSProp(_, _, _, Some vref) -> vref.XmlDoc
        | FSProp(_, _, None, None) -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            let lines = pi.PUntaint((fun pix -> (pix :> IProvidedCustomAttributeProvider).GetXmlDocAttributes(pi.TypeProvider.PUntaintNoFailure id)), m)
            XmlDoc (lines, m)
#endif

    /// Get the TcGlobals associated with the object
    member x.TcGlobals =
        match x with
        | ILProp ilpinfo -> ilpinfo.TcGlobals
        | FSProp(g, _, _, _) -> g
#if !NO_TYPEPROVIDERS
        | ProvidedProp(amap, _, _) -> amap.g
#endif

    /// Indicates if the enclosing type for the property is a value type.
    ///
    /// For an extension property, this indicates if the property extends a struct type.
    member x.IsValueType = isStructTy x.TcGlobals x.ApparentEnclosingType

    /// Get the result type of the property
    member x.GetPropertyType (amap, m) =
        match x with      
        | ILProp ilpinfo -> ilpinfo.GetPropertyType (amap, m)
        | FSProp (g, _, Some vref, _)
        | FSProp (g, _, _, Some vref) ->
            let ty = x.ApparentEnclosingAppType
            let inst = GetInstantiationForPropertyVal g (ty, vref)
            ReturnTypeOfPropertyVal g vref.Deref |> instType inst

        | FSProp _ -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, m) ->
            ImportProvidedType amap m (pi.PApply((fun pi -> pi.PropertyType), m))
#endif

    /// Get the names and types of the indexer parameters associated with the property
    ///
    /// If the property is in a generic type, then the type parameters are instantiated in the types returned.
    member x.GetParamNamesAndTypes(amap, m) =
        match x with     
        | ILProp ilpinfo -> ilpinfo.GetParamNamesAndTypes(amap, m)
        | FSProp (g, ty, Some vref, _)
        | FSProp (g, ty, _, Some vref) ->
            let inst = GetInstantiationForPropertyVal g (ty, vref)
            ArgInfosOfPropertyVal g vref.Deref |> List.map (ParamNameAndType.FromArgInfo >> ParamNameAndType.Instantiate inst)
        | FSProp _ -> failwith "unreachable"
#if !NO_TYPEPROVIDERS
        | ProvidedProp (_, pi, m) ->
            [ for p in pi.PApplyArray((fun pi -> pi.GetIndexParameters()), "GetIndexParameters", m) do
                let paramName = p.PUntaint((fun p -> match p.Name with "" -> None | s -> Some (mkSynId m s)), m)
                let paramTy = ImportProvidedType amap m (p.PApply((fun p -> p.ParameterType), m))
                yield ParamNameAndType(paramName, paramTy) ]
#endif

    /// Get the details of the indexer parameters associated with the property
    member x.GetParamDatas(amap, m) =
        x.GetParamNamesAndTypes(amap, m)
        |> List.map (fun (ParamNameAndType(nmOpt, paramTy)) -> ParamData(false, false, false, NotOptional, NoCallerInfo, nmOpt, ReflectedArgInfo.None, paramTy))

    /// Get the types of the indexer parameters associated with the property
    member x.GetParamTypes(amap, m) =  
      x.GetParamNamesAndTypes(amap, m) |> List.map (fun (ParamNameAndType(_, ty)) -> ty)

    /// Get a MethInfo for the 'getter' method associated with the property
    member x.GetterMethod =
        match x with
        | ILProp ilpinfo -> ILMeth(x.TcGlobals, ilpinfo.GetterMethod, None)
        | FSProp(g, ty, Some vref, _) -> FSMeth(g, ty, vref, None)
#if !NO_TYPEPROVIDERS
        | ProvidedProp(amap, pi, m) ->
            let meth = GetAndSanityCheckProviderMethod m pi (fun pi -> pi.GetGetMethod()) FSComp.SR.etPropertyCanReadButHasNoGetter
            ProvidedMeth(amap, meth, None, m)

#endif
        | FSProp _ -> failwith "no getter method"

    /// Get a MethInfo for the 'setter' method associated with the property
    member x.SetterMethod =
        match x with
        | ILProp ilpinfo -> ILMeth(x.TcGlobals, ilpinfo.SetterMethod, None)
        | FSProp(g, ty, _, Some vref) -> FSMeth(g, ty, vref, None)
#if !NO_TYPEPROVIDERS
        | ProvidedProp(amap, pi, m) ->
            let meth = GetAndSanityCheckProviderMethod m pi (fun pi -> pi.GetSetMethod()) FSComp.SR.etPropertyCanWriteButHasNoSetter
            ProvidedMeth(amap, meth, None, m)
#endif
        | FSProp _ -> failwith "no setter method"

    /// Test whether two property infos have the same underlying definition.
    /// Uses the same techniques as 'MethInfosUseIdenticalDefinitions'.
    /// Must be compatible with ItemsAreEffectivelyEqual relation.
    static member PropInfosUseIdenticalDefinitions x1 x2 =

        let optVrefEq g = function
          | Some vref1, Some vref2 -> valRefEq g vref1 vref2
          | None, None -> true
          | _ -> false

        match x1, x2 with
        | ILProp ilpinfo1, ILProp ilpinfo2 -> (ilpinfo1.RawMetadata === ilpinfo2.RawMetadata)
        | FSProp(g, _, vrefa1, vrefb1), FSProp(_, _, vrefa2, vrefb2) ->
            optVrefEq g (vrefa1, vrefa2) && optVrefEq g (vrefb1, vrefb2)
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi1, _), ProvidedProp(_, pi2, _) -> ProvidedPropertyInfo.TaintedEquals (pi1, pi2)
#endif
        | _ -> false

    /// Indicates if the property is a IsABC union case tester implied by a union case definition
    member x.IsUnionCaseTester =
        x.HasGetter &&
        x.GetterMethod.IsUnionCaseTester

    /// Calculates a hash code of property info. Must be compatible with ItemsAreEffectivelyEqual relation.
    member pi.ComputeHashCode() =
        match pi with
        | ILProp ilpinfo -> hash ilpinfo.RawMetadata.Name
        | FSProp(_, _, vrefOpt1, vrefOpt2) ->
            // Hash on string option * string option
            let vth = (vrefOpt1 |> Option.map (fun vr -> vr.LogicalName), (vrefOpt2 |> Option.map (fun vref -> vref.LogicalName)))
            hash vth
#if !NO_TYPEPROVIDERS
        | ProvidedProp(_, pi, _) -> ProvidedPropertyInfo.TaintedGetHashCode pi
#endif

    override x.ToString() = "property " + x.PropertyName

//-------------------------------------------------------------------------
// ILEventInfo


/// Describes an F# use of an event backed by Abstract IL metadata
[<NoComparison; NoEquality>]
type ILEventInfo =
    | ILEventInfo of ilTypeInfo: ILTypeInfo * ilEventDef: ILEventDef

    /// Get the enclosing ("parent"/"declaring") type of the field.
    member x.ApparentEnclosingType = match x with ILEventInfo(tinfo, _) -> tinfo.ToType

    // Note: events are always associated with nominal types
    member x.ApparentEnclosingAppType = x.ApparentEnclosingType

    // Note: IL Events are never extension members as C# has no notion of extension events as yet
    member x.DeclaringTyconRef = tcrefOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    member x.TcGlobals = match x with ILEventInfo(tinfo, _) -> tinfo.TcGlobals

    /// Get the raw Abstract IL metadata for the event
    member x.RawMetadata = match x with ILEventInfo(_, ed) -> ed

    /// Get the declaring IL type of the event as an ILTypeInfo
    member x.ILTypeInfo = match x with ILEventInfo(tinfo, _) -> tinfo

    /// Get the ILMethInfo describing the 'add' method associated with the event
    member x.AddMethod =
        let mdef = resolveILMethodRef x.ILTypeInfo.RawMetadata x.RawMetadata.AddMethod
        ILMethInfo(x.TcGlobals, IlType x.ILTypeInfo, mdef, [])

    /// Get the ILMethInfo describing the 'remove' method associated with the event
    member x.RemoveMethod =
        let mdef = resolveILMethodRef x.ILTypeInfo.RawMetadata x.RawMetadata.RemoveMethod
        ILMethInfo(x.TcGlobals, IlType x.ILTypeInfo, mdef, [])

    /// Get the declaring type of the event as an ILTypeRef
    member x.TypeRef = x.ILTypeInfo.ILTypeRef

    /// Get the name of the event
    member x.EventName = x.RawMetadata.Name

    /// Indicates if the property is static
    member x.IsStatic = x.AddMethod.IsStatic

    override x.ToString() = !!x.ILTypeInfo.ToString() + "::" + x.EventName

//-------------------------------------------------------------------------
// Helpers for EventInfo

/// An exception type used to raise an error using the old error system.
///
/// Error text: "A definition to be compiled as a .NET event does not have the expected form. Only property members can be compiled as .NET events."
exception BadEventTransformation of range

/// Properties compatible with type IDelegateEvent and attributed with CLIEvent are special:
/// we generate metadata and add/remove methods
/// to make them into a .NET event, and mangle the name of a property.
/// We don't handle static, indexer or abstract properties correctly.
/// Note the name mangling doesn't affect the name of the get/set methods for the property
/// and so doesn't affect how we compile F# accesses to the property.
let private tyConformsToIDelegateEvent g ty =
   isIDelegateEventType g ty && isDelegateTy g (destIDelegateEventType g ty)


/// Create an error object to raise should an event not have the shape expected by the .NET idiom described further below
let nonStandardEventError nm m =
    Error ((FSComp.SR.eventHasNonStandardType(nm, ("add_"+nm), ("remove_"+nm))), m)

/// Find the delegate type that an F# event property implements by looking through the type hierarchy of the type of the property
/// for the first instantiation of IDelegateEvent.
let FindDelegateTypeOfPropertyEvent g amap nm m ty =
    match SearchEntireHierarchyOfType (tyConformsToIDelegateEvent g) g amap m ty with
    | None -> error(nonStandardEventError nm m)
    | Some ty -> destIDelegateEventType g ty


//-------------------------------------------------------------------------
// EventInfo

/// Describes an F# use of an event
[<NoComparison; NoEquality>]
type EventInfo =
    /// An F# use of an event backed by F#-declared metadata
    | FSEvent of tcGlobals: TcGlobals * propInfo: PropInfo * addMethod: ValRef * removeMethod: ValRef

    /// An F# use of an event backed by .NET metadata
    | ILEvent of ilEventInfo: ILEventInfo

#if !NO_TYPEPROVIDERS
    /// An F# use of an event backed by provided metadata
    | ProvidedEvent of amap: ImportMap * providedEvent: Tainted<ProvidedEventInfo> * range: range
#endif

    /// Get the enclosing type of the event.
    ///
    /// If this is an extension member, then this is the apparent parent, i.e. the type the event appears to extend.
    member x.ApparentEnclosingType =
        match x with
        | ILEvent ileinfo -> ileinfo.ApparentEnclosingType
        | FSEvent (_, p, _, _) -> p.ApparentEnclosingType
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (amap, ei, m) -> ImportProvidedType amap m (ei.PApply((fun ei -> nonNull<ProvidedType> ei.DeclaringType), m))
#endif

    /// Get the enclosing type of the method info, using a nominal type for tuple types
    member x.ApparentEnclosingAppType =
        match x with
        | ILEvent ileinfo -> ileinfo.ApparentEnclosingAppType
        | _ -> x.ApparentEnclosingType

    member x.ApparentEnclosingTyconRef = tcrefOfAppTy x.TcGlobals x.ApparentEnclosingAppType

    /// Get the declaring type or module holding the method.
    /// Note that C#-style extension properties don't exist in the C# design as yet.
    /// If this is an F#-style extension method it is the logical module
    /// holding the value for the extension method.
    member x.DeclaringTyconRef =
        match x.ArbitraryValRef with
        | Some vref when x.IsExtensionMember && vref.HasDeclaringEntity -> vref.DeclaringEntity
        | _ -> x.ApparentEnclosingTyconRef

    /// Indicates if this event has an associated XML comment authored in this assembly.
    member x.HasDirectXmlComment =
        match x with
        | FSEvent (_, p, _, _) -> p.HasDirectXmlComment
#if !NO_TYPEPROVIDERS
        | ProvidedEvent _ -> true
#endif
        | _ -> false

    /// Get the intra-assembly XML documentation for the property.
    member x.XmlDoc =
        match x with
        | ILEvent _ -> XmlDoc.Empty
        | FSEvent (_, p, _, _) -> p.XmlDoc
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei, m) ->
            let lines = ei.PUntaint((fun eix -> (eix :> IProvidedCustomAttributeProvider).GetXmlDocAttributes(ei.TypeProvider.PUntaintNoFailure id)), m)
            XmlDoc (lines, m)
#endif

    /// Get the logical name of the event.
    member x.EventName =
        match x with
        | ILEvent ileinfo -> ileinfo.EventName
        | FSEvent (_, p, _, _) -> p.PropertyName
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei, m) -> ei.PUntaint((fun ei -> ei.Name), m)
#endif

     /// Get the event name in DisplayName form
    member x.DisplayName =
        match x with
        | FSEvent (_, p, _, _) -> p.DisplayName
        | _ -> x.EventName |> PrettyNaming.ConvertValLogicalNameToDisplayName false

     /// Get the event name in DisplayNameCore form
    member x.DisplayNameCore =
        match x with
        | FSEvent (_, p, _, _) -> p.DisplayNameCore
        | _ -> x.EventName |> PrettyNaming.ConvertValLogicalNameToDisplayNameCore

    /// Indicates if this property is static.
    member x.IsStatic =
        match x with
        | ILEvent ileinfo -> ileinfo.IsStatic
        | FSEvent (_, p, _, _) -> p.IsStatic
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei, m) ->
            let meth = GetAndSanityCheckProviderMethod m ei (fun ei -> ei.GetAddMethod()) FSComp.SR.etEventNoAdd
            meth.PUntaint((fun mi -> mi.IsStatic), m)
#endif

    /// Indicates if this is an extension member
    member x.IsExtensionMember =
        match x with
        | ILEvent _ -> false
        | FSEvent (_, p, _, _) -> p.IsExtensionMember
#if !NO_TYPEPROVIDERS
        | ProvidedEvent _ -> false
#endif

    /// Get the TcGlobals associated with the object
    member x.TcGlobals =
        match x with
        | ILEvent ileinfo -> ileinfo.TcGlobals
        | FSEvent(g, _, _, _) -> g
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (amap, _, _) -> amap.g
#endif

    /// Indicates if the enclosing type for the event is a value type.
    ///
    /// For an extension event, this indicates if the event extends a struct type.
    member x.IsValueType = isStructTy x.TcGlobals x.ApparentEnclosingType

    /// Get the 'add' method associated with an event
    member x.AddMethod =
        match x with
        | ILEvent ileinfo -> ILMeth(ileinfo.TcGlobals, ileinfo.AddMethod, None)
        | FSEvent(g, p, addValRef, _) -> FSMeth(g, p.ApparentEnclosingType, addValRef, None)
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (amap, ei, m) ->
            let meth = GetAndSanityCheckProviderMethod m ei (fun ei -> ei.GetAddMethod()) FSComp.SR.etEventNoAdd
            ProvidedMeth(amap, meth, None, m)
#endif

    /// Get the 'remove' method associated with an event
    member x.RemoveMethod =
        match x with
        | ILEvent ileinfo -> ILMeth(x.TcGlobals, ileinfo.RemoveMethod, None)
        | FSEvent(g, p, _, removeValRef) -> FSMeth(g, p.ApparentEnclosingType, removeValRef, None)
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (amap, ei, m) ->
            let meth = GetAndSanityCheckProviderMethod m ei (fun ei -> ei.GetRemoveMethod()) FSComp.SR.etEventNoRemove
            ProvidedMeth(amap, meth, None, m)
#endif

    /// Try to get an arbitrary F# ValRef associated with the member. This is to determine if the member is virtual, amongst other things.
    member x.ArbitraryValRef: ValRef option =
        match x with
        | FSEvent(_, _, addValRef, _) -> Some addValRef
        | _ ->  None

    /// Get the delegate type associated with the event.
    member x.GetDelegateType(amap, m) =
        match x with
        | ILEvent(ILEventInfo(tinfo, edef)) ->
            // Get the delegate type associated with an IL event, taking into account the instantiation of the
            // declaring type
            if Option.isNone edef.EventType then error (nonStandardEventError x.EventName m)
            let nullness = {DirectAttributes = AttributesFromIL(edef.MetadataIndex,edef.CustomAttrsStored); Fallback = tinfo.NullableClassSource}
            ImportILTypeFromMetadata amap m tinfo.ILScopeRef tinfo.TypeInstOfRawMetadata [] nullness edef.EventType.Value

        | FSEvent(g, p, _, _) ->
            FindDelegateTypeOfPropertyEvent g amap x.EventName m (p.GetPropertyType(amap, m))
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei, _) ->
            ImportProvidedType amap m (ei.PApply((fun ei -> ei.EventHandlerType), m))
#endif

    /// Test whether two event infos have the same underlying definition.
    /// Must be compatible with ItemsAreEffectivelyEqual relation.
    static member EventInfosUseIdenticalDefinitions x1 x2 =
        match x1, x2 with
        | FSEvent(g, pi1, vrefa1, vrefb1), FSEvent(_, pi2, vrefa2, vrefb2) ->
            PropInfo.PropInfosUseIdenticalDefinitions pi1 pi2 && valRefEq g vrefa1 vrefa2 && valRefEq g vrefb1 vrefb2
        | ILEvent ileinfo1, ILEvent ileinfo2 -> (ileinfo1.RawMetadata === ileinfo2.RawMetadata)
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei1, _), ProvidedEvent (_, ei2, _) -> ProvidedEventInfo.TaintedEquals (ei1, ei2)
#endif
        | _ -> false

    /// Calculates a hash code of event info (similar as previous)
    /// Must be compatible with ItemsAreEffectivelyEqual relation.
    member ei.ComputeHashCode() =
        match ei with
        | ILEvent ileinfo -> hash ileinfo.RawMetadata.Name
        | FSEvent(_, pi, vref1, vref2) -> hash ( pi.ComputeHashCode(), vref1.LogicalName, vref2.LogicalName)
#if !NO_TYPEPROVIDERS
        | ProvidedEvent (_, ei, _) -> ProvidedEventInfo.TaintedGetHashCode ei
#endif
    override x.ToString() = "event " + x.EventName
    
    /// Get custom attributes for events (only applicable for IL events)
    member x.GetCustomAttrs() =
        match x with
        | ILEvent(ILEventInfo(_, ilEventDef))-> ilEventDef.CustomAttrs
        | _ -> ILAttributes.Empty

//-------------------------------------------------------------------------
// Helpers associated with getting and comparing method signatures

/// Strips inref and outref to be a byref.
let stripByrefTy g ty =
    if isByrefTy g ty then mkByrefTy g (destByrefTy g ty)
    else ty

/// Represents the information about the compiled form of a method signature. Used when analyzing implementation
/// relations between members and abstract slots.
type CompiledSig = CompiledSig of argTys: TType list list * returnTy: TType option * formalMethTypars: Typars * formalMethTyparInst: TyparInstantiation

/// Get the information about the compiled form of a method signature. Used when analyzing implementation
/// relations between members and abstract slots.
let CompiledSigOfMeth g amap m (minfo: MethInfo) =
    let formalMethTypars = minfo.FormalMethodTypars
    let fminst = generalizeTypars formalMethTypars
    let vargTys = minfo.GetParamTypes(amap, m, fminst)
    let vrty = minfo.GetCompiledReturnType(amap, m, fminst)

    // The formal method typars returned are completely formal - they don't take into account the instantiation
    // of the enclosing type. For example, they may have constraints involving the _formal_ type parameters
    // of the enclosing type. This instantiations can be used to interpret those type parameters
    let fmtpinst =
        let parentTyArgs = argsOfAppTy g minfo.ApparentEnclosingAppType
        let memberParentTypars  = minfo.GetFormalTyparsOfDeclaringType m
        mkTyparInst memberParentTypars parentTyArgs

    CompiledSig(vargTys, vrty, formalMethTypars, fmtpinst)

/// Inref and outref parameter types will be treated as a byref type for equivalency.
let MethInfosEquivByPartialSig erasureFlag ignoreFinal g amap m (minfo: MethInfo) (minfo2: MethInfo) =
    (minfo.GenericArity = minfo2.GenericArity) &&
    (ignoreFinal || minfo.IsFinal = minfo2.IsFinal) &&
    let formalMethTypars = minfo.FormalMethodTypars
    let fminst = generalizeTypars formalMethTypars
    let formalMethTypars2 = minfo2.FormalMethodTypars
    let fminst2 = generalizeTypars formalMethTypars2
    let argTys = minfo.GetParamTypes(amap, m, fminst)
    let argTys2 = minfo2.GetParamTypes(amap, m, fminst2)
    (argTys, argTys2) ||> List.lengthsEqAndForall2 (List.lengthsEqAndForall2 (fun ty1 ty2 ->
        typeAEquivAux erasureFlag g (TypeEquivEnv.EmptyIgnoreNulls.FromEquivTypars formalMethTypars formalMethTypars2) (stripByrefTy g ty1) (stripByrefTy g ty2)))

/// Used to hide/filter members from super classes based on signature
/// Inref and outref parameter types will be treated as a byref type for equivalency.
let MethInfosEquivByNameAndPartialSig erasureFlag ignoreFinal g amap m (minfo: MethInfo) (minfo2: MethInfo) =
    (minfo.LogicalName = minfo2.LogicalName) &&
    MethInfosEquivByPartialSig erasureFlag ignoreFinal g amap m minfo minfo2

/// Used to hide/filter members from base classes based on signature
let PropInfosEquivByNameAndPartialSig erasureFlag g amap m (pinfo: PropInfo) (pinfo2: PropInfo) =
    pinfo.PropertyName = pinfo2.PropertyName &&
    let argTys = pinfo.GetParamTypes(amap, m)
    let argTys2 = pinfo2.GetParamTypes(amap, m)
    List.lengthsEqAndForall2 (typeEquivAux erasureFlag g) argTys argTys2

/// Used to hide/filter members from base classes based on signature
let MethInfosEquivByNameAndSig erasureFlag ignoreFinal g amap m minfo minfo2 =
    MethInfosEquivByNameAndPartialSig erasureFlag ignoreFinal g amap m minfo minfo2 &&
    let (CompiledSig(_, retTy, formalMethTypars, _)) = CompiledSigOfMeth g amap m minfo
    let (CompiledSig(_, retTy2, formalMethTypars2, _)) = CompiledSigOfMeth g amap m minfo2
    match retTy, retTy2 with
    | None, None -> true
    | Some retTy, Some retTy2 -> typeAEquivAux erasureFlag g (TypeEquivEnv.EmptyIgnoreNulls.FromEquivTypars formalMethTypars formalMethTypars2) retTy retTy2
    | _ -> false

/// Used to hide/filter members from super classes based on signature
let PropInfosEquivByNameAndSig erasureFlag g amap m (pinfo: PropInfo) (pinfo2: PropInfo) =
    PropInfosEquivByNameAndPartialSig erasureFlag g amap m pinfo pinfo2 &&
    let retTy = pinfo.GetPropertyType(amap, m)
    let retTy2 = pinfo2.GetPropertyType(amap, m)
    typeEquivAux erasureFlag g retTy retTy2

let SettersOfPropInfos (pinfos: PropInfo list) = pinfos |> List.choose (fun pinfo -> if pinfo.HasSetter then Some(pinfo.SetterMethod, Some pinfo) else None)

let GettersOfPropInfos (pinfos: PropInfo list) = pinfos |> List.choose (fun pinfo -> if pinfo.HasGetter then Some(pinfo.GetterMethod, Some pinfo) else None)

[<return: Struct>]
let (|DifferentGetterAndSetter|_|) (pinfo: PropInfo) =
    if not (pinfo.HasGetter && pinfo.HasSetter) then
        ValueNone
    else
        match pinfo.GetterMethod.ArbitraryValRef, pinfo.SetterMethod.ArbitraryValRef with
        | Some getValRef, Some setValRef ->
            if getValRef.Accessibility <> setValRef.Accessibility then
                ValueSome (getValRef, setValRef)
            else
                match getValRef.ValReprInfo with
                | Some getValReprInfo when
                    // Getter has an index parameter
                    getValReprInfo.TotalArgCount > 1  -> ValueSome (getValRef, setValRef)
                | _ -> ValueNone 
        | _ -> ValueNone