#r "packages/Mono.Cecil/lib/net40/Mono.Cecil.dll"
#r "packages/QuickGraph/lib/net4/QuickGraph.dll"
#r "packages/FAKE/tools/FakeLib.dll"
open Mono.Cecil
open System.IO
open QuickGraph
open System.Linq
open System.Reflection
open Fake

let folder = @"C:\Program Files (x86)\DevExpress 14.2\Components\Bin\Framework"

let baseName = "DevExpress."

let downloadNuget () =
    use client = new System.Net.WebClient()

    client.DownloadFile("https://dist.nuget.org/win-x86-commandline/latest/nuget.exe", "./nuget.exe")

let downloadNugetIfNotExist () =
    if File.Exists("./nuget.exe") |> not then downloadNuget ()

type AssemblyFile = { File: FileInfo; Name: string; FullName: string; Dependencies: string list; Version: string }

let getVersion (assembly: AssemblyDefinition) =
  let versionAttributeTypeName = typeof<AssemblyFileVersionAttribute>.FullName
  match assembly.CustomAttributes.FirstOrDefault(fun f ->f.AttributeType.FullName = versionAttributeTypeName) with
  | null -> None
  | a -> Some (a.ConstructorArguments.First().Value :?> string)

let convertToAssemblyFile baseName (file: FileInfo) =
    let assembly = AssemblyDefinition.ReadAssembly(file.FullName)

    {
        File = file
        Name = assembly.Name.Name
        FullName = assembly.FullName
        Dependencies = assembly.MainModule.AssemblyReferences |> Seq.map (fun r -> r.FullName) |> Seq.filter (fun r -> r.StartsWith(baseName)) |> Seq.toList
        Version = getVersion assembly |> Option.get
    }

let searchAllAssemblies baseName (folder: DirectoryInfo) =
    folder.GetFiles(baseName + "*.dll") |> Seq.map (convertToAssemblyFile baseName)

let addDependenciesInGraph addEdge (assembliesByFullName: System.Collections.Generic.IDictionary<string, AssemblyFile>) (assembly: AssemblyFile) =
    assembly.Dependencies 
    |> Seq.map (fun d -> assembliesByFullName.[d])
    |> Seq.iter (fun d -> addEdge(assembly, d))

let toGraph assemblies =
    let graph = new QuickGraph.AdjacencyGraph<AssemblyFile, IEdge<AssemblyFile>>()
    
    let assembliesByFullName = assemblies |> Seq.map (fun a -> a.FullName, a) |> dict

    assembliesByFullName.Values |> graph.AddVertexRange |> ignore

    assembliesByFullName.Values |> Seq.iter (addDependenciesInGraph (fun d -> graph.AddEdge(new Edge<_>(d)) |> ignore) assembliesByFullName)

    graph

let reverseDependencyOrder graph =
  let a = new Algorithms.TopologicalSort.TopologicalSortAlgorithm<_,_>(graph)
  a.Compute()
  a.SortedVertices
  |> Seq.rev

let publish (templateFile: FileInfo) (output: DirectoryInfo) (graph: QuickGraph.AdjacencyGraph<AssemblyFile, IEdge<AssemblyFile>>) (assemblies: AssemblyFile seq) =
    
    for a in assemblies do

        let deps = 
          match graph.TryGetOutEdges(a) with
          | false, _ -> []
          | true, deps ->
            deps
            |> Seq.map (fun e -> e.Target.Name, e.Target.Version)
            |> Seq.toList

        let templateFileName = Path.Combine(output.FullName, a.Name + "." + templateFile.Name)

        templateFile.CopyTo(templateFileName)
        
        let fileNames = [
          let assemblyFile = a.File
          let docFile = new FileInfo(assemblyFile.FullName.Replace(".dll", ".xml"))
          yield (assemblyFile.FullName, Some "lib/net40/", None)
          if docFile.Exists then
            yield (docFile.FullName, Some "lib/net40", None)
        ]
      
        Fake.NuGetHelper.NuGet (
            fun p -> 
              {
                p with
                  Files = fileNames
                  OutputPath = output.FullName
                  WorkingDir = output.FullName
                  Dependencies = deps
                  ToolPath = Path.Combine(__SOURCE_DIRECTORY__, "nuget.exe")
                  Description = a.Name
                  Authors = ["DevExpress"]
                  Version = a.Version
                  Project = a.Name
              }
          ) templateFileName

        trace a.Name

let outputFolder = new DirectoryInfo(Path.Combine(__SOURCE_DIRECTORY__, "nugetpackages", baseName))
let templateFile = FileInfo(Path.Combine(__SOURCE_DIRECTORY__, @"template.devexpress.nuspec"))

if outputFolder.Exists then outputFolder.Delete(true)
outputFolder.Create()

downloadNugetIfNotExist ()
searchAllAssemblies baseName (DirectoryInfo(folder))
|> toGraph
|> (fun g -> g, reverseDependencyOrder g)
|> (fun (g, a) -> publish templateFile outputFolder g a)
