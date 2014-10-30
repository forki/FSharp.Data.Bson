﻿(* Copyright (c) 2013-2014 Tomas Petricek and Gustavo Guerra
 * Copyright (c) 2014 Max Hirschhorn
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

// -----------------------------------------------------------------------------
// BSON type provider - generates code for accessing inferred elements
// -----------------------------------------------------------------------------

namespace BsonProvider.ProviderImplementation

open System
open System.Collections.Generic
open Microsoft.FSharp.Quotations
open MongoDB.Bson
open FSharp.Data.Runtime
open FSharp.Data.Runtime.StructuralTypes
open ProviderImplementation
open ProviderImplementation.ProvidedTypes
open BsonProvider.Runtime
open BsonProvider.ProviderImplementation.BsonConversionsGenerator

#nowarn "10001"

/// Context that is used to generate the BSON types
type BsonGenerationContext =
    {
        TypeProviderType : ProvidedTypeDefinition
        UniqueNiceName : string -> string // to nameclash type names
        IBsonTopType : Type
        BsonValueType : Type
        BsonRuntimeType : Type
        TypeCache : Dictionary<InferedType, ProvidedTypeDefinition>
        GenerateConstructors : bool
    }

    static member Create(tpType, ?uniqueNiceName, ?typeCache) =
        let uniqueNiceName = defaultArg uniqueNiceName (NameUtils.uniqueGenerator NameUtils.nicePascalName)
        let typeCache = defaultArg typeCache (Dictionary())
        BsonGenerationContext.Create(tpType, uniqueNiceName, typeCache, true)

    static member Create(tpType, uniqueNiceName, typeCache, generateConstructors) =
        {
            TypeProviderType = tpType
            UniqueNiceName = uniqueNiceName
            IBsonTopType = typeof<IBsonTop>
            BsonValueType = typeof<BsonValue>
            BsonRuntimeType = typeof<BsonRuntime>
            TypeCache = typeCache
            GenerateConstructors = generateConstructors
        }

    member x.MakeOptionType(typ:Type) =
        typedefof<option<_>>.MakeGenericType typ

type BsonGenerationResult =
    {
        ConvertedType : Type
        Converter : (Expr -> Expr) option
    }

    member x.GetConverter ctx =
        defaultArg x.Converter id

    member x.ConverterFunc ctx =
        ReflectionHelpers.makeDelegate (x.GetConverter ctx) ctx.IBsonTopType

    member x.ConvertedTypeErased ctx =
        if x.ConvertedType.IsArray then
            match x.ConvertedType.GetElementType() with
            | :? ProvidedTypeDefinition -> ctx.IBsonTopType.MakeArrayType()
            | _ -> x.ConvertedType
        else
            match x.ConvertedType with
            | :? ProvidedTypeDefinition -> ctx.IBsonTopType
            | _ -> x.ConvertedType

[<AutoOpen>]
module private ActivePatterns =

    let (|EmptyArray|_|) = function
    | InferedType.Collection (_, EmptyMap InferedType.Top typ) -> Some typ
    | _ -> None

    let (|SingletonArray|_|) = function
    | InferedType.Collection (_, SingletonMap (_, (_, typ))) -> Some typ
    | _ -> None

    let (|ArrayOfOptionals|_|) inferedType =

        let (|MapWithNull|_|) (map:Map<_,_>) =
            if map.Count = 2 then
                match Map.toList map with
                | [ (InferedTypeTag.Null, _); elem ]
                | [ elem; (InferedTypeTag.Null, _) ] -> Some elem
                | _ -> None
            else None

        match inferedType with
        | InferedType.Collection (_, MapWithNull (_, (_, typ))) -> Some typ
        | _ -> None

module BsonTypeBuilder =

    let private (?) = QuotationBuilder.(?)

    let private inferType typ =
        { ConvertedType = typ
          Converter = None }

    let private inferCollection (elemType:Type) conv =
        { ConvertedType = elemType.MakeArrayType()
          Converter = Some conv }

    let private failIfOptionalRecord = function
    | InferedType.Record (_, _, true) as inferedType ->
        failwithf "Expected non-optional record, but received %A" inferedType
    | _ -> ()

    // check if a type was already created for the inferedType before creating a new one
    let private getOrCreateType ctx inferedType createType =

        // normalize properties of the inferedType which don't affect code generation
        let rec normalize topLevel = function
        | InferedType.Record (_, props, optional) ->
            let props = props |> List.map (fun prop ->
                { prop with Type = normalize false prop.Type })

            // Optionality of records only affects their container,
            // so always set it to true at the top level
            InferedType.Record (None, props, optional || topLevel)

        | InferedType.Collection (order, types) ->
            let types = types |> Map.map (fun _ (mult, inferedType) ->
                mult, normalize false inferedType)
            InferedType.Collection (order, types)

        | InferedType.Heterogeneous cases ->
            cases
            |> Map.map (fun _ inferedType -> normalize false inferedType)
            |> InferedType.Heterogeneous

        | inferedType -> inferedType

        let inferedType = normalize true inferedType
        match ctx.TypeCache.TryGetValue inferedType with
        | true, typ -> inferType typ
        | false, _ ->
            let typ = createType()
            ctx.TypeCache.Add(inferedType, typ)
            inferType typ

    let private replaceWithBsonValue (ctx:BsonGenerationContext) typ =
        if typ = ctx.IBsonTopType then
            ctx.BsonValueType
        elif typ.IsArray && typ.GetElementType() = ctx.IBsonTopType then
            ctx.BsonValueType.MakeArrayType()
        elif typ.IsGenericType && typ.GetGenericArguments() = [| ctx.IBsonTopType |] then
            typ.GetGenericTypeDefinition().MakeGenericType ctx.BsonValueType
        else
            typ

    let rec generateBsonType ctx nameOverride = function
    | InferedType.Primitive (typ, unit, optional) ->

        let typ, conv =
            PrimitiveInferedProperty.Create("", typ, optional, unit)
            |> convertBsonValue

        { ConvertedType = typ
          Converter = Some conv }

    | InferedType.Record (name, props, optional) as inferedType -> getOrCreateType ctx inferedType <| fun () ->

        let name =
            if String.IsNullOrEmpty nameOverride
            then match name with Some name -> name | _ -> "Record"
            else nameOverride
            |> ctx.UniqueNiceName

        // Generate new type for the record
        let objectTy = ProvidedTypeDefinition(name, Some ctx.IBsonTopType, HideObjectMethods = true)
        ctx.TypeProviderType.AddMember(objectTy)

        // to nameclash property names
        let makeUnique = NameUtils.uniqueGenerator NameUtils.nicePascalName
        makeUnique "BsonValue" |> ignore

        // Add all record fields as properties
        let members =
            [ for prop in props ->

                let optionalityHandledByProperty =
                    match prop.Type with
                    | InferedType.Primitive (_, _, optional) -> optional
                    | _ -> false

                let propResult = generateBsonType ctx "" prop.Type
                let propName = prop.Name

                let getter = fun (Singleton doc) ->
                    if prop.Type.IsOptional then
                        ctx.BsonRuntimeType?ConvertOptionalProperty (propResult.ConvertedTypeErased ctx) (doc, propName, propResult.ConverterFunc ctx) :> Expr
                    else
                        propResult.GetConverter ctx <@@ BsonRuntime.GetPropertyPacked(%%doc, propName) @@>

                let convertedType =
                    if prop.Type.IsOptional && not optionalityHandledByProperty then
                        ctx.MakeOptionType propResult.ConvertedType
                    else propResult.ConvertedType

                let name = makeUnique prop.Name
                prop.Name,
                ProvidedProperty(name, convertedType, GetterCode = getter),
                ProvidedParameter(NameUtils.niceCamelName name, replaceWithBsonValue ctx convertedType) ]

        let names, properties, parameters = List.unzip3 members
        objectTy.AddMembers properties

        if ctx.GenerateConstructors then

            objectTy.AddMember <|
                ProvidedConstructor(parameters, InvokeCode = fun args ->
                    let properties =
                        Expr.NewArray(typeof<string * obj>,
                                      args
                                      |> List.mapi (fun i a -> Expr.NewTuple [ Expr.Value names.[i]
                                                                               Expr.Coerce(a, typeof<obj>) ]))
                    <@@ BsonRuntime.CreateDocument(%%properties) @@>)

            let hasBsonValueType =
                match parameters with
                | [ prop ] -> prop.ParameterType = ctx.BsonValueType
                | _ -> false

            if not hasBsonValueType then
                objectTy.AddMember <|
                        ProvidedConstructor(
                            [ProvidedParameter("bsonValue", ctx.BsonValueType)],
                            InvokeCode = fun (Singleton arg) ->
                                <@@ BsonTop.Create((%%arg:BsonValue), "") @@>)

        objectTy

    | SingletonArray typ ->
        failIfOptionalRecord typ
        let elemResult = generateBsonType ctx nameOverride typ

        let conv = fun (doc:Expr) ->
            let t = elemResult.ConvertedTypeErased ctx
            let args = (doc, elemResult.ConverterFunc ctx)
            ctx.BsonRuntimeType?ConvertArray t args

        inferCollection elemResult.ConvertedType conv

    | ArrayOfOptionals typ ->
        failIfOptionalRecord typ
        let elemResult = generateBsonType ctx nameOverride typ

        let conv = fun (doc:Expr) ->
            let t = elemResult.ConvertedTypeErased ctx
            let args = (doc, elemResult.ConverterFunc ctx)
            ctx.BsonRuntimeType?ConvertArrayOfOptionals t args

        let convertedType = ctx.MakeOptionType elemResult.ConvertedType
        inferCollection convertedType conv

    | EmptyArray _
    | InferedType.Collection _ ->

        let conv = fun (doc:Expr) -> <@@ BsonRuntime.ConvertArray(%%doc) @@>
        inferCollection ctx.IBsonTopType conv

    | InferedType.Top
    | InferedType.Heterogeneous _ -> inferType ctx.IBsonTopType

    | InferedType.Null -> inferType (ctx.MakeOptionType ctx.IBsonTopType)

    | InferedType.Json _ -> failwith "JSON type not supported"
