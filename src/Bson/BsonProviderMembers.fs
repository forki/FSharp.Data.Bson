namespace BsonProvider.ProviderImplementation

open System
open System.IO
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open MongoDB.Bson
open MongoDB.Bson.Serialization
open ProviderImplementation.ProvidedTypes
open BsonProvider.Runtime
open BsonProvider.Runtime.IO

#nowarn "10001"

[<AutoOpen>]
module internal Helpers =

    // Carries part of the information needed to generate the type
    type TypeProviderSpec =
        { // the generated type
          GeneratedType : ProvidedTypeDefinition
          // the representation type (may or may not be the same type as what is returned by the constructor)
          RepresentationType : Type
          // the constructor from a stream to the representation
          CreateFromStream : Expr<Stream> -> Expr }

    let invalidChars = [ for c in "\"|<>{}[]," -> c ] @ [ for i in 0..31 -> char i ] |> set

    let tryGetUri str =
        match Uri.TryCreate(str, UriKind.RelativeOrAbsolute) with
        | false, _ -> None
        | true, uri ->
            if str.Trim() = "" || not uri.IsAbsoluteUri && Seq.exists invalidChars.Contains str
            then None else Some uri

    let getPath path (resolver:UriResolver) =
        match tryGetUri path with
        | None -> failwith "path does not represent a location"
        | Some uri ->
            match resolver.TryResolveToPath uri with
            | None -> failwith "path could not be resolved as a file"
            | Some path -> path

module internal Members =

    /// Generates the static GetSamples() method
    let private createGetSamplesMember (spec:TypeProviderSpec) path (resolver:UriResolver) =

        let resolver = { resolver with ResolutionType = DesignTime }
        let path = resolver |> getPath path

        let returnType = spec.RepresentationType.MakeArrayType()
        let m = ProvidedMethod("GetSamples", [], returnType, IsStaticMethod = true)

        m.InvokeCode <- fun _ ->
            <@ File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read) :> Stream @>
            |> spec.CreateFromStream

        m.AddXmlDoc "Returns the entire set of sample BSON documents"
        m

    let createAllMembers (cfg:TypeProviderConfig) (spec:TypeProviderSpec)
                         path resolutionFolder resource =

        let resolver =
            { ResolutionType = Runtime
              DefaultResolutionFolder = cfg.ResolutionFolder
              ResolutionFolder = resolutionFolder }

        [ createGetSamplesMember spec path resolver :> MethodInfo ]
