﻿module internal FSharpApiSearch.SignatureMatcher

open FSharpApiSearch.EngineTypes
open FSharpApiSearch.SpecialTypes
open FSharpApiSearch.StringPrinter

type SignatureMatcher = ILowTypeMatcher -> SignatureQuery -> ApiSignature -> Context -> MatchingResult

module Rules =
  let choiceRule (runRules: SignatureMatcher, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Choice (_, choices, _)), _ ->
      EngineDebug.WriteLine("choice rule.")
      EngineDebug.WriteLine(sprintf "test %A" (choices |> List.map (fun x -> x.Debug())))
      choices
      |> Seq.map (fun c ->
        EngineDebug.WriteLine(sprintf "try test %s" (c.Debug()))
        EngineDebug.Indent()
        let result = runRules lowTypeMatcher (SignatureQuery.Signature c) right ctx
        EngineDebug.Unindent()
        result
      )
      |> Seq.tryPick (fun result -> match result with Matched _ as m -> Some m | _ -> None)
      |> function
        | Some matched -> matched
        | None -> Failure FailureInfo.None
    | _ -> Continue

  let moduleValueRule (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _), ApiSignature.ModuleValue _ -> Continue
    | SignatureQuery.Signature left, ApiSignature.ModuleValue right ->
      EngineDebug.WriteLine("module value rule.")
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

  let (|WildcardOrVariable|_|) = function
    | Wildcard _ -> Some ()
    | Variable _ -> Some ()
    | _ -> None

  let trimOptionalParameters ((leftParams, _): Arrow) (right: Function) : Function =
    match right with
    | [ nonCurriedParameters ], ret ->
      let leftLength = (leftParams |> List.sumBy (function Tuple ({ Elements = xs }, _) -> xs.Length | _ -> 1))
      let rightLength = nonCurriedParameters.Length
      if leftLength < rightLength then
        let trimedParameters, extraParameters = List.splitAt leftLength nonCurriedParameters
        if List.forall (fun x -> x.IsOptional) extraParameters then
          EngineDebug.WriteLine(sprintf "trimed %d parameters." (rightLength - leftLength))
          [ trimedParameters ], ret
        else
          right
      else
        right
    | _ ->
      right

  type TestArrow = ILowTypeMatcher -> Arrow -> Function -> Context -> MatchingResult

  let testArrow (lowTypeMatcher: ILowTypeMatcher) (left: Arrow) (right: Function) ctx =
    EngineDebug.WriteLine("test arrow.")
    let right = trimOptionalParameters left right
    let test ctx =
      let right = Function.toArrow right
      lowTypeMatcher.TestArrow left right ctx
    match (fst left), (fst right) with
    | [ WildcardOrVariable ], [ [ _ ] ] -> test ctx
    | [ WildcardOrVariable ], [ _ ] -> Failure FailureInfo.None
    | _ -> test ctx

  let (|Right_CurriedFunction|_|) (right: Function) =
    let ps, ret = right
    if ps |> List.forall (function [ _ ] -> true | _ -> false) then
      Some (ps |> List.map (fun x -> x.Head.Type), ret.Type)
    else
      None

  let (|Right_NonCurriedFunction|_|) (right: Function) =
    match right with
    | ([ parameters ], ret) when parameters.Length >= 2 -> Some (parameters |> List.map (fun x -> x.Type), ret.Type)
    | _ -> None

  let (|Right_TupleFunction|_|) (right: Function) =
    match right with
    | [ [ { Type = Tuple ({ Elements = parameters }, _) } ] ], { Type = ret } -> Some (parameters, ret)
    | _ -> None

  let (|Left_CurriedFunction|_|) (left: Arrow) =
    match left with
    | [ Tuple _ ], _ -> None
    | _ -> Some left

  let (|Left_NonCurriedFunction|_|) (left: Arrow) =
    match left with
    | [ Tuple ({ Elements = parameters }, _) ], ret -> Some (parameters, ret)
    | _ -> None

  let testArrow_IgnoreParamStyle (lowTypeMatcher: ILowTypeMatcher) (left: Arrow) (right: Function) ctx =
    EngineDebug.WriteLine("test arrow (ignore parameter style).")
    let right = trimOptionalParameters left right
    match left, right with
    | ([ _ ], _), ([ [ _ ] ], _) ->
      EngineDebug.WriteLine("pattern 1")
      lowTypeMatcher.TestArrow left (Function.toArrow right) ctx
    | Left_NonCurriedFunction left, Right_CurriedFunction right ->
      EngineDebug.WriteLine("pattern 2")
      lowTypeMatcher.TestArrow left right ctx
      |> MatchingResult.mapMatched (Context.addDistance "parameter style" 1)
    | Left_CurriedFunction left, (Right_TupleFunction right | Right_NonCurriedFunction right) ->
      EngineDebug.WriteLine("pattern 3")
      lowTypeMatcher.TestArrow left right ctx
      |> MatchingResult.mapMatched (Context.addDistance "parameter style" 1)
    | _, _ ->
      EngineDebug.WriteLine("pattern 4")
      testArrow lowTypeMatcher left right ctx
      
  let moduleFunctionRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow (left, _)), ApiSignature.ModuleFunction right
    | SignatureQuery.Signature (LowType.Patterns.AbbreviationRoot (Arrow (left, _))), ApiSignature.ModuleFunction right ->
      EngineDebug.WriteLine("module function rule.")
      testArrow lowTypeMatcher left right ctx
    | SignatureQuery.Signature leftRet, ApiSignature.ModuleFunction right ->
      EngineDebug.WriteLine("module function rule.")
      let left = [], leftRet
      testArrow lowTypeMatcher left right ctx
    | SignatureQuery.Signature (Arrow _ as left), ApiSignature.ModuleValue (LowType.Patterns.AbbreviationRoot (Arrow _ as right)) ->
      EngineDebug.WriteLine("module function rule.")
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

  let arrowQueryAndDelegateRule (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _ as left), ApiSignature.ModuleValue (Delegate (_, rightBody, rightPos)) ->
      let right = Arrow (rightBody, rightPos)
      EngineDebug.WriteLine("arrow query and delegate rule.")
      lowTypeMatcher.Test left right ctx
      |> MatchingResult.mapMatched (Context.addDistance "arrow and delegate" 1)
    | _ -> Continue

  let activePatternRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow (leftElems, _)), ApiSignature.ActivePatten (_, rightElems) ->
      EngineDebug.WriteLine("active pattern rule.")
      testArrow lowTypeMatcher leftElems rightElems ctx
    | _ -> Continue

  let breakArrow = function
    | Arrow (xs, _) -> xs
    | other -> [], other

  let (|StaticMember|_|) = function
    | ApiSignature.StaticMember (_, member') -> Some member'
    | ApiSignature.TypeExtension { MemberModifier = MemberModifier.Static; Member = member' } -> Some member'
    | ApiSignature.ExtensionMember member' -> Some member'
    | _ -> None

  let (|NoArgsMember|_|) = function
    | { Parameters = [] } as m -> Some m
    | _ -> None

  let extensionMemberRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow (([ leftReceiver; leftParams ], leftReturnType), _)), ApiSignature.ExtensionMember ({ Parameters = [ rightReceiver :: rightParams ] } as member' ) ->
      EngineDebug.WriteLine("extension member rule.")
      let left = [ leftParams ], leftReturnType
      let right = { member' with Parameters = [ rightParams ] }
      lowTypeMatcher.TestReceiver leftReceiver rightReceiver.Type ctx
      |> MatchingResult.bindMatched (testArrow lowTypeMatcher left (Member.toFunction right))
    | _ -> Continue

  let staticMemberRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _), StaticMember (NoArgsMember _) ->
      EngineDebug.WriteLine("Arrow and static no args member do not match.")
      Failure FailureInfo.None
    | SignatureQuery.Signature left, StaticMember member' ->
      EngineDebug.WriteLine("static member rule.")
      testArrow lowTypeMatcher (breakArrow left) (Member.toFunction member') ctx
    | _ -> Continue

  let constructorRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature left, ApiSignature.Constructor (_, member') ->
      EngineDebug.WriteLine("constructor rule.")
      testArrow lowTypeMatcher (breakArrow left) (Member.toFunction member') ctx 
    | _ -> Continue

  let (|InstanceMember|_|) = function
    | ApiSignature.InstanceMember (declaringType, member') -> Some (declaringType, member')
    | ApiSignature.TypeExtension { MemberModifier = MemberModifier.Instance; ExistingType = declaringType; Member = member' } -> Some (declaringType, member')
    | _ -> None

  let arrowAndInstanceMemberRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow (((leftReceiver :: leftMemberParams), leftMemberRet), _)), InstanceMember (declaringType, member') ->
      EngineDebug.WriteLine("arrow and instance member rule.")
      lowTypeMatcher.TestReceiver leftReceiver declaringType ctx
      |> MatchingResult.bindMatched (testArrow lowTypeMatcher (leftMemberParams, leftMemberRet) (Member.toFunction member'))
    | _ -> Continue

  let unionCaseRule (testArrow: TestArrow) (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow (leftElems, _)), ApiSignature.UnionCase uc
    | SignatureQuery.Signature (LowType.Patterns.AbbreviationRoot (Arrow (leftElems, _))), ApiSignature.UnionCase uc when uc.Fields.IsEmpty = false ->
      EngineDebug.WriteLine("union case rule.")
      let caseAsFunc = UnionCase.toFunction uc
      testArrow lowTypeMatcher leftElems caseAsFunc ctx
    | SignatureQuery.Signature left, ApiSignature.UnionCase { DeclaringType = right; Fields = [] } ->
      EngineDebug.WriteLine("union case rule.")
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

  let typeDefRule (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _ | Wildcard _ | Variable _), ApiSignature.FullTypeDefinition _ -> Failure FailureInfo.None
    | SignatureQuery.Signature left, ApiSignature.FullTypeDefinition typeDef ->
      EngineDebug.WriteLine("type def rule.")
      let right = typeDef.LowType
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

  let moduleDefRule (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _ | Wildcard _ | Variable _ | LowType.Subtype _), ApiSignature.ModuleDefinition _ -> Failure FailureInfo.None
    | SignatureQuery.Signature left, ApiSignature.ModuleDefinition moduleDef ->
      EngineDebug.WriteLine("module def rule.")
      let right = moduleDef.LowType
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

  let typeAbbreviationRule (_, lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx =
    match left, right with
    | SignatureQuery.Signature (Arrow _ | Wildcard _ | Variable _), ApiSignature.TypeAbbreviation _ -> Failure FailureInfo.None
    | SignatureQuery.Signature (LowType.Subtype _ as left), ApiSignature.TypeAbbreviation { Original = right } ->
      EngineDebug.WriteLine("type abbreviation rule.")
      lowTypeMatcher.Test left right ctx
    | SignatureQuery.Signature left, ApiSignature.TypeAbbreviation abbreviationDef ->
      EngineDebug.WriteLine("type abbreviation rule.")
      let abbreviation = abbreviationDef.TypeAbbreviation
      let dummy = abbreviation.Original
      let right = Choice.create (dummy, [ abbreviation.Abbreviation; abbreviation.Original ])
      lowTypeMatcher.Test left right ctx
    | _ -> Continue

let tryGetSignatureQuery = function
  | QueryMethod.BySignature s -> Some s
  | QueryMethod.ByName (_, s) -> Some s
  | QueryMethod.ByNameOrSignature (_, s) -> Some s
  | QueryMethod.ByActivePattern _ -> None
  | QueryMethod.ByComputationExpression _ -> None

let instance (options: SearchOptions) =
  let testArrow : Rules.TestArrow =
    match options.IgnoreParameterStyle with
    | Enabled -> Rules.testArrow_IgnoreParamStyle
    | Disabled -> Rules.testArrow

  let rule =
    Rule.compose [|
      yield Rules.choiceRule

      yield Rules.moduleValueRule
      yield Rules.moduleFunctionRule testArrow
      yield Rules.activePatternRule testArrow

      yield Rule.failureToContinue (Rules.extensionMemberRule testArrow)
      yield Rules.staticMemberRule testArrow
      yield Rules.constructorRule testArrow
        
      yield Rules.arrowAndInstanceMemberRule testArrow
        
      yield Rules.arrowQueryAndDelegateRule

      yield Rules.unionCaseRule testArrow

      yield Rules.typeDefRule
      yield Rules.moduleDefRule
      yield Rules.typeAbbreviationRule

      yield Rule.terminator
    |]

  let rec run (lowTypeMatcher: ILowTypeMatcher) (left: SignatureQuery) (right: ApiSignature) ctx = 
    Rule.run rule (run, lowTypeMatcher) left right ctx

  { new IApiMatcher with
      member this.Name = "Signature Matcher"
      member this.Test lowTypeMatcher query api ctx =
        match tryGetSignatureQuery query.Method with
        | Some (SignatureQuery.Wildcard) -> Matched ctx
        | Some s -> run lowTypeMatcher s api.Signature ctx
        | None -> Matched ctx }