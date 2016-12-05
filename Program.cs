using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;

namespace UnderscoreLambdasGithub
{
    public class Program
    {
        //static readonly Dictionary<string, int> underscoreRepos = new Dictionary<string, int>();

        public static void Main()
        {
            //Repos = GetRepos().ToArray();

            var commonOptions = new ExecutionDataflowBlockOptions { BoundedCapacity = 1 };
            var parallelOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 2,
                MaxDegreeOfParallelism = 2,
                EnsureOrdered = false
            };

            var cloneBlock = new TransformBlock<string, string>(repo => Clone(repo), parallelOptions);

            var getFilesBlock = new TransformManyBlock<string, string>(path => GetFiles(path), commonOptions);

            getFilesBlock.SendAsync(BasePath).Wait();

            var parseBlock = new TransformBlock<string, LambdaData>(file => Parse(file), commonOptions);

            LambdaData lambdaDataSummary = new LambdaData();
            var summarizeBlock = new ActionBlock<LambdaData>(ld => lambdaDataSummary.Add(ld));

            var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

            cloneBlock.LinkTo(getFilesBlock, linkOptions);
            getFilesBlock.LinkTo(parseBlock, linkOptions);
            parseBlock.LinkTo(summarizeBlock, linkOptions);

            var sendDataTask = SendData(cloneBlock);

            summarizeBlock.Completion.Wait();

            sendDataTask.Wait();

            Print(lambdaDataSummary);

            Console.WriteLine();

            //Print(underscoreRepos);
        }

        private static void Print(LambdaData lambdaData)
        {
            Console.WriteLine($"single lambdas: {lambdaData.TotalSingleLambdasCount}, multi lambdas: {lambdaData.TotalMultiLambdasCount}");

            Console.WriteLine("Single lambdas:");
            Print(lambdaData.SingleLambdas);

            Console.WriteLine("Multi lambdas, one parameter:");
            Print(lambdaData.MultiLambdasOneParameter);

            Console.WriteLine("Multi lambdas, multi parameters:");
            Print(lambdaData.MultiLambdasMultiParameters);
        }

        private static void Print(Dictionary<string, int> dictionary)
        {
            Console.WriteLine($"Total: {dictionary.Values.Sum()}");

            var data = dictionary.OrderByDescending(kvp => kvp.Value).Take(10);

            foreach (var kvp in data)
            {
                Console.WriteLine($"{kvp.Key}\t{kvp.Value}");
            }
        }

        private static readonly string BasePath = @"C:\Users\Svick\AppData\Local\Temp\UnderscoreLambdasGithub\";

        private static async Task SendData(ITargetBlock<string> targetBlock)
        {
            foreach (var repo in Repos ?? new string[0])
            {
                await targetBlock.SendAsync(repo);
            }

            targetBlock.Complete();
        }

        private static LambdaData Parse(string file)
        {
            Console.WriteLine($"Parsing {file.Substring(BasePath.Length)}.");

            var lambdaData = new LambdaData();

            var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(File.OpenRead(file)));
            var semanticModel = CSharpCompilation.Create(null).AddSyntaxTrees(syntaxTree).GetSemanticModel(syntaxTree);

            var lambdas = syntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<LambdaExpressionSyntax>();

            foreach (var lambda in lambdas)
            {
                var names = lambda.DescendantNodes().OfType<NameSyntax>().Select(name => semanticModel.GetSymbolInfo(name).Symbol).Where(s => s != null).ToList();

                var simpleLambda = lambda as SimpleLambdaExpressionSyntax;
                if (simpleLambda != null)
                {
                    var paramaterSymbol = semanticModel.GetDeclaredSymbol(simpleLambda.Parameter);

                    if (SingleLambda(lambdaData, names, paramaterSymbol))
                    {
                        //LambdaData.Increment(underscoreRepos, file.Substring(BasePath.Length).Split('\\')[0]);
                    }
                }

                var complexLambda = lambda as ParenthesizedLambdaExpressionSyntax;
                if (complexLambda != null)
                {
                    var parameterSymbols = complexLambda.ParameterList.Parameters
                        .Select(param => semanticModel.GetDeclaredSymbol(param))
                        .ToList();

                    if (parameterSymbols.Count == 1)
                    {
                        if (SingleLambda(lambdaData, names, parameterSymbols.Single()))
                        {
                            //LambdaData.Increment(underscoreRepos, file.Substring(BasePath.Length).Split('\\')[0]);
                        }
                    }
                    else
                    {
                        MultiLambda(lambdaData, names, parameterSymbols);
                    }
                }
            }

            return lambdaData;
        }

        private static void MultiLambda(LambdaData lambdaData, List<ISymbol> names, List<IParameterSymbol> parameterSymbols)
        {
            lambdaData.MultiLambda();

            var missingSymbols =
                (from parameterSymbol in parameterSymbols
                 where !names.Contains(parameterSymbol)
                 select parameterSymbol.Name).ToList();

            switch (missingSymbols.Count)
            {
                case 0:
                    break;
                case 1:
                    lambdaData.MultiLambdaOneUnused(missingSymbols.Single());
                    break;
                default:
                    lambdaData.MultiLambdaMultiUnused(string.Join(", ", missingSymbols));
                    break;
            }
        }

        private static bool SingleLambda(LambdaData lambdaData, List<ISymbol> names, IParameterSymbol parameterSymbol)
        {
            lambdaData.SingleLambda();

            if (!names.Contains(parameterSymbol))
            {
                lambdaData.SingleLambdaUnused(parameterSymbol.Name);

                if (parameterSymbol.Name == "_")
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> GetFiles(string path)
            => Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);

        private static string Clone(string repo)
        {
            Console.WriteLine(repo);

            string gitUrl = $"https://github.com/{repo}.git";
            string path = Path.Combine(BasePath, repo.Split('/')[1]);

            if (Directory.Exists(path))
                Console.WriteLine($"Directory for {repo} already exists.");
            else
                Process.Start("git", $"clone {gitUrl} {path} --depth 1").WaitForExit();

            return path;
        }

        private static string[] Repos;

        static IEnumerable<string> GetRepos()
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd("gsvick at gmail.com");

            var jsonString = client.GetStringAsync(
                "https://api.github.com/search/repositories?q=language:csharp+stars:%3C=100&sort=updated&per_page=100").Result;

            dynamic data = JsonConvert.DeserializeObject(jsonString);
            foreach (var repo in data.items)
            {
                yield return repo.full_name;
            }
        }
    }

    class LambdaData
    {
        public int TotalSingleLambdasCount { get; private set; }
        public int TotalMultiLambdasCount { get; private set; }

        public Dictionary<string, int> SingleLambdas { get; } = new Dictionary<string, int>();
        public Dictionary<string, int> MultiLambdasOneParameter { get; } = new Dictionary<string, int>();
        public Dictionary<string, int> MultiLambdasMultiParameters { get; } = new Dictionary<string, int>();

        public void SingleLambda() => TotalSingleLambdasCount++;
        public void MultiLambda() => TotalMultiLambdasCount++;

        public static void Increment<T>(Dictionary<T, int> dictionary, T key, int added = 1)
        {
            int value;
            dictionary.TryGetValue(key, out value);
            dictionary[key] = value + added;
        }

        public void SingleLambdaUnused(string name) => Increment(SingleLambdas, name);
        public void MultiLambdaOneUnused(string name) => Increment(MultiLambdasOneParameter, name);
        public void MultiLambdaMultiUnused(string name) => Increment(MultiLambdasMultiParameters, name);

        private static void Add(Dictionary<string, int> thisDictionary, Dictionary<string, int> otherDictionary)
        {
            foreach (var kvp in otherDictionary)
            {
                Increment(thisDictionary, kvp.Key, kvp.Value);
            }
        }

        public void Add(LambdaData other)
        {
            this.TotalSingleLambdasCount += other.TotalSingleLambdasCount;
            this.TotalMultiLambdasCount += other.TotalMultiLambdasCount;

            Add(this.SingleLambdas, other.SingleLambdas);
            Add(this.MultiLambdasOneParameter, other.MultiLambdasOneParameter);
            Add(this.MultiLambdasMultiParameters, other.MultiLambdasMultiParameters);
        }
    }
}
