﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2017.

#I __SOURCE_DIRECTORY__
#load "Interactive.fsx"
open System
open System.IO
open System.Reflection
open Prime
open global.Nu

type ParameterConversionDetails =
    | NormalParameter of Type
    | NormalSeqParameter of Type
    | RelationParameter of Type
    | RelationSeqParameter of Type

type ParameterConversion =
    | ValueParameter of ParameterConversionDetails
    | WorldParameter

type ReturnConversionDetails =
    | NormalReturn of Type
    | NormalListReturn of Type
    | SimulantReturn
    | SimulantHashSetReturn

type ReturnConversion =
    | PureReturn of ReturnConversionDetails
    | MixedReturn of ReturnConversionDetails
    | ImpureReturn

type FunctionBinding =
    { FunctionName : string
      FunctionBindingName : string
      FunctionParameters : (string * ParameterConversion) array
      FunctionReturn : ReturnConversion }

let getParameterConversion (ty : Type) =
    match ty.Name with
    | "World" -> WorldParameter
    | "Entity" | "Layer" | "Screen" | "Game" | "Simulant" -> ValueParameter (RelationParameter ty)
    | "IEnumerable`1" | "FSharpList`1" ->
        let itemType = (ty.GetGenericArguments ()).[0]
        if not (typeof<Simulant>.IsAssignableFrom itemType)
        then ValueParameter (NormalSeqParameter ty)
        else ValueParameter (RelationSeqParameter ty)
    | _ -> ValueParameter (NormalParameter ty)

let rec tryGetReturnConversion (ty : Type) : ReturnConversion option =
    match ty.Name with
    | "World" -> Some ImpureReturn
    | "Tuple`2" ->
        let gargs = ty.GetGenericArguments ()
        match gargs with
        | [|garg; garg2|] when garg2.Name = "World" ->
            match
                (match garg.Name with
                 | "Entity" | "Layer" | "Screen" | "Game" | "Simulant" -> Some SimulantReturn
                 | "IEnumerable`1" | "FSharpList`1"| "HashSet`1"  ->
                     let itemType = (garg.GetGenericArguments ()).[0]
                     if typeof<Simulant>.IsAssignableFrom itemType
                     then Some SimulantHashSetReturn
                     else Some (NormalListReturn garg)
                 | _ -> Some (NormalReturn garg)) with
            | Some conversion ->
                let subconversionOpts = Array.map tryGetReturnConversion gargs
                match Array.definitizePlus subconversionOpts with
                | (true, subconversions) ->
                    match (tryGetReturnConversion garg, tryGetReturnConversion garg2) with
                    | (Some rc, Some rc2) -> Some (MixedReturn conversion)
                    | (_, _) -> None
                | (false, _) -> None
            | None -> None
        | _ -> None
    | "IEnumerable`1" | "FSharpList`1" | "HashSet`1" ->
        let itemType = (ty.GetGenericArguments ()).[0]
        if not (typeof<Simulant>.IsAssignableFrom itemType)
        then Some (PureReturn (NormalListReturn ty))
        else Some (PureReturn SimulantHashSetReturn)
    | "Entity" | "Layer" | "Screen" | "Game" | "Simulant" -> Some (PureReturn SimulantReturn)
    | _ -> Some (PureReturn (NormalReturn ty))

let tryGenerateBinding (method : MethodInfo) =
    let functionName = method.Name.Replace(".Static", "").Replace("World.", "")
    let functionBindingName =
        match (method.GetCustomAttribute<FunctionBindingAttribute> ()).BindingName with
        | "" -> functionName
        | bindingName -> bindingName
    let pars = method.GetParameters ()
    let parTypes = Array.map (fun (pi : ParameterInfo) -> pi.ParameterType) pars
    let parNames = Array.map (fun (par : ParameterInfo) -> par.Name) pars
    let conversions = Array.map getParameterConversion parTypes
    let returnType = method.ReturnType
    match tryGetReturnConversion returnType with
    | Some returnConversion ->
        Some
            { FunctionName = functionName
              FunctionBindingName = functionBindingName
              FunctionParameters = Array.zip parNames conversions
              FunctionReturn = returnConversion }
    | None -> None

let generateParameterList (functionParameters : (string * ParameterConversion) array)=
    let parNames = Array.map fst functionParameters
    String.Join (" ", parNames)

let generateNormalParameterConversion (par : string) (ty : Type) =
    "            let " + par + " = ScriptingWorld.tryExport typeof<" + ty.GetGenericName () + "> " + par + " world |> Option.get :?> " + ty.GetGenericName () + "\n"

let generateNormalListParameterConversion (par : string) (ty : Type) =
    "            let struct (" + par + ", world) =\n" +
    "                match World.evalInternal " + par + " world with\n" +
    "                | struct (Scripting.List list, world) ->\n" +
    "                    Seq.fold (fun struct (values, world) value ->\n" +
    "                        let value = ScriptingWorld.tryExport typeof<" + ty.GetGenericName () + "> value world |> Option.get :?> " + ty.GetGenericName () + "\n" +
    "                        struct (value :: values, world))\n" +
    "                        struct ([], world)\n" +
    "                        list\n" +
    "                | struct (Scripting.Violation (_, error, _), _) -> failwith error\n" +
    "                | struct (_, _) -> failwith \"Relation must be either a String or Keyword.\"\n"

let generateRelationParameterConversion (par : string) (ty : Type) =
    let addressToSimulant = if ty.Name = "Simulant" then "World.deriveSimulant" else ty.Name
    "            let struct (" + par + ", world) =\n" +
    "                let context = World.getScriptContext world\n" +
    "                match World.evalInternal " + par + " world with\n" +
    "                | struct (Scripting.String str, world)\n" +
    "                | struct (Scripting.Keyword str, world) ->\n" +
    "                    let relation = Relation.makeFromString str\n" +
    "                    let address = Relation.resolve context.SimulantAddress relation\n" +
    "                    struct (" + addressToSimulant + " address, world)\n" +
    "                | struct (Scripting.Violation (_, error, _), _) -> failwith error\n" +
    "                | struct (_, _) -> failwith \"Relation must be either a String or Keyword.\"\n"

let generateRelationListParameterConversion (par : string) (ty : Type) =
    let addressToSimulant = if ty.Name = "Simulant" then "World.deriveSimulant" else ty.Name
    "            let struct (" + par + ", world) =\n" +
    "                let context = World.getScriptContext world\n" +
    "                match World.evalInternal " + par + " world with\n" +
    "                | struct (Scripting.List simulants, world) ->\n" +
    "                    List.fold (fun struct (simulants, world) simulant ->\n" +
    "                        match simulant with\n" +
    "                        | Scripting.String str\n" +
    "                        | Scripting.Keyword str ->\n" +
    "                            let relation = Relation.makeFromString str\n" +
    "                            let address = Relation.resolve context.SimulantAddress relation\n" +
    "                            struct (" + addressToSimulant + " address :: simulants, world)\n" +
    "                        | Scripting.Violation (_, error, _) -> failwith error\n" +
    "                        | _ -> failwith \"Relation must be either a String or Keyword.\")\n" +
    "                        struct ([], world)\n" +
    "                        simulants\n" +
    "                | struct (Scripting.Violation (_, error, _), _) -> failwith error\n" +
    "                | struct (_, _) -> failwith \"Expecting a list of relations.\"\n"

let rec generateParameterConversionOpt (par : string) conversion =
    match conversion with
    | ValueParameter rc ->
        match rc with
        | NormalParameter ty -> Some (generateNormalParameterConversion par ty)
        | NormalSeqParameter ty -> Some (generateNormalListParameterConversion par (ty.GetGenericArguments ()).[0])
        | RelationParameter ty -> Some (generateRelationParameterConversion par ty)
        | RelationSeqParameter ty -> Some (generateRelationListParameterConversion par (ty.GetGenericArguments ()).[0])
    | WorldParameter -> None

let generateBindingFunction binding =
    
    let functionAndExceptionHeader =
        "    let " + binding.FunctionBindingName + " " + generateParameterList binding.FunctionParameters + " =\n" +
        "        let oldWorld = world\n" +
        "        try\n"
    
    let conversions =
        Array.map (fun (par, conversion) -> generateParameterConversionOpt par conversion) binding.FunctionParameters |>
        Array.definitize |>
        fun conversions -> String.Join ("", conversions)
    
    let returnConversion =
        match binding.FunctionReturn with
        | ImpureReturn -> "            struct (Scripting.Unit, result)\n"
        | MixedReturn rc ->
            match rc with
            | NormalReturn ty ->
                "            let (value, world) = result\n" +
                "            let value = ScriptingWorld.tryImport typeof<" + ty.GetGenericName () + "> value world |> Option.get\n" +
                "            struct (value, world)\n"
            | NormalListReturn ty ->
                "            let (value, world) = result\n" +
                "            let value = ScriptingWorld.tryImport typeof<" + ty.GetGenericName () + "> value world |> Option.get\n" +
                "            struct (value, world)\n"
            | SimulantReturn ->
                "            let (value, world) = result\n" +
                "            let value = Scripting.String (scstring value)\n" +
                "            struct (value, world)\n"
            | SimulantHashSetReturn ->
                "            let (value, world) = result\n" +
                "            let value = Scripting.Ring (Set.ofSeq (Seq.map (scstring >> Scripting.String) value))\n" +
                "            struct (value, world)\n"
        | PureReturn rc ->
            match rc with
            | NormalReturn ty ->
                "            let value = result\n" +
                "            let value = ScriptingWorld.tryImport typeof<" + ty.GetGenericName () + "> value world |> Option.get\n" +
                "            struct (value, world)\n"
            | NormalListReturn ty ->
                "            let value = result\n" +
                "            let value = ScriptingWorld.tryImport typeof<" + ty.GetGenericName () + "> value world |> Option.get\n" +
                "            struct (value, world)\n"
            | SimulantReturn ->
                "            let value = result\n" +
                "            let value = Scripting.String (scstring value)\n" +
                "            struct (value, world)\n"
            | SimulantHashSetReturn ->
                "            let value = result\n" +
                "            let value = Scripting.Ring (Set.ofSeq (Seq.map (scstring >> Scripting.String) value))\n" +
                "            struct (value, world)\n"
    
    let invocation =
        "            let result = World." + binding.FunctionName + " " + generateParameterList binding.FunctionParameters + "\n"
    
    let exceptionHandler =
        "        with exn ->\n" +
        "            let violation = Scripting.Violation ([\"InvalidBindingInvocation\"], \"Could not invoke binding '" + binding.FunctionBindingName + "' due to: \" + scstring exn, None)\n" +
        "            struct (violation, World.choose oldWorld)\n"
    
    functionAndExceptionHeader +
    conversions +
    invocation +
    returnConversion +
    exceptionHandler

let generateBindingMatcher binding =
    
    let args =
        binding.FunctionParameters |>
        Array.filter (function (_, WorldParameter) -> false | _ -> true) |>
        Array.map fst
    
    let argArray = "[|" + String.Join ("; ", args) + "|]"
    "        | \"" + binding.FunctionBindingName + "\" ->\n" +
    "            match World.evalManyInternal exprs world with\n" +
    "            | struct (" + argArray + " as args, world) when Array.notExists (function Scripting.Violation _ -> true | _ -> false) args ->\n" +
    "                " + binding.FunctionBindingName + " " + String.Join (" ", args) + " world\n" +
    "            | _ ->\n" +
    "                let violation = Scripting.Violation ([\"InvalidBindingInvocation\"], \"Incorrect number of arguments for binding '" + binding.FunctionBindingName + "' at:\\n\" + SymbolOrigin.tryPrint originOpt, None)\n" +
    "                struct (violation, world)\n"

let generateBindingsMatcher bindings =
    
    let bindingMatchers =
        bindings |>
        Array.map generateBindingMatcher |>
        fun bindings -> String.Join ("", bindings)
    
    "    let evalBinding fnName exprs originOpt world =\n" +
    "        match fnName with\n" +
    bindingMatchers +
    "        | _ ->\n" +
    "            let violation = Scripting.Violation ([\"InvalidBindingInvocation\"], \"No binding exists for '\" + " + "fnName" + " + \"' at:\\n\" + SymbolOrigin.tryPrint originOpt, None)\n" +
    "            struct (violation, world)\n"

let generateIsBindingPredicate bindings =
    "    let isBinding fnName =\n" +
    "        match fnName with\n" +
    (bindings |> Array.map (fun binding -> "        | \"" + binding.FunctionBindingName + "\"") |> fun bindingList -> String.Join ("\n", bindingList)) + " -> true\n" +
    "        | _ -> false\n"

let generateBindingSyntax bindings =

    let bindingLists =
        bindings |>
        Array.map (fun binding -> binding.FunctionBindingName) |>
        (fun arr -> Array.splitInto ((Array.length arr) / 4) arr) |>
        Array.map (fun bindingList -> "        \"" + String.Join (" ", bindingList)) |>
        (fun bindingLists -> String.Join (" \" +\n", bindingLists) + "\"\n")

    "    let [<Literal>] BindingKeywords =\n" + bindingLists

let generateBindingsCode bindings =

    let header =
        "// Nu Game Engine.\n" +
        "// Copyright (C) Bryan Edds, 2013-2017.\n" +
        "\n" +
        "//*********************************************************************************************//\n" +
        "//                                                                                             //\n" +
        "// NOTE: This code is GENERATED by 'GenerateWorldBindings.fsx'! Do NOT edit this code by hand! //\n" +
        "//                                                                                             //\n" +
        "//*********************************************************************************************//\n" +
        "\n" +
        "namespace Nu\n" +
        "open System\n" +
        "open System.Collections.Generic\n" +
        "open OpenTK\n" +
        "open Prime\n" +
        "open global.Nu\n" +
        "\n" +
        "[<RequireQualifiedAccess>]\n" +
        "module WorldBindings =\n" +
        "\n"

    let bindingSyntax =
        generateBindingSyntax bindings + "\n"

    let isBindingPredicate =
        generateIsBindingPredicate bindings + "\n"

    let bindingCodes =
        bindings |>
        Array.map generateBindingFunction |>
        fun strs -> String.Join ("\n", strs) + "\n"

    let bindingsMatcher =
        generateBindingsMatcher bindings

    header +
    bindingSyntax +
    isBindingPredicate +
    bindingCodes +
    bindingsMatcher

let types =
    AppDomain.CurrentDomain.GetAssemblies () |>
    Array.filter (fun asm -> (asm.GetName ()).Name = "Nu") |>
    Array.head |>
    fun asm -> asm.GetTypes () |> Array.filter (fun ty -> isNotNull (ty.GetCustomAttribute<ModuleBindingAttribute> ()))

let bindings =
    types |>
    Array.map (fun (ty : Type) -> ty.GetMethods ()) |>
    Array.concat |>
    Array.filter (fun mi -> isNotNull (mi. GetCustomAttribute<FunctionBindingAttribute> ())) |>
    Array.map tryGenerateBinding |>
    Array.definitize // TODO: error output

let code =
    generateBindingsCode bindings |>
    fun code -> File.WriteAllText ("../../WorldBindings.fs", code)