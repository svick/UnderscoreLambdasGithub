using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;

namespace UnderscoreLambdasGithub
{
    public static class Program
    {
        public static void Main()
        {
            Repos = GetRepos(10).Distinct().ToArray();

            Console.WriteLine($"{Repos.Length} repos");

            var commonOptions = new ExecutionDataflowBlockOptions { BoundedCapacity = 1 };
            var parallelOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 4,
                MaxDegreeOfParallelism = 4,
                EnsureOrdered = false
            };

            var cloneBlock = new TransformBlock<string, string>(repo => Clone(repo), parallelOptions);

            var getFilesBlock = new TransformManyBlock<string, string>(path => GetFiles(path), commonOptions);

            var parseBlock = new TransformBlock<string, Data>(file => Parse(file), commonOptions);

            Data dataSummary = new Data();
            var summarizeBlock = new ActionBlock<Data>(ld => dataSummary.Add(ld));

            var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

            cloneBlock.LinkTo(getFilesBlock, linkOptions);
            getFilesBlock.LinkTo(parseBlock, linkOptions);
            parseBlock.LinkTo(summarizeBlock, linkOptions);

            var sendDataTask = SendData(cloneBlock);

            //getFilesBlock.SendAsync(BasePath).Wait();
            //cloneBlock.Complete();

            summarizeBlock.Completion.Wait();

            sendDataTask.Wait();

            Print(dataSummary);

            Console.WriteLine();
        }

        private static void Print(Data lambdaData)
        {
            Console.WriteLine("Single lambdas:");
            Console.WriteLine($"Total: {lambdaData.TotalSingleLambdasCount}");
            Console.WriteLine($"Underscore: {lambdaData.TotalSingleLambdaUnderscoreCount}");
            Console.WriteLine($"Underscore unused: {lambdaData.TotalSingleLambdaUnderscoreUnusedCount}");
            Console.WriteLine();

            Console.WriteLine("Multi lambdas:");
            Console.WriteLine($"Total: {lambdaData.TotalMultiLambdasCount}");
            Console.WriteLine($"Underscore: {lambdaData.TotalMultiLambdaUnderscoreCount}");
            Console.WriteLine($"Underscore unused: {lambdaData.TotalMultiLambdaUnderscoreUnusedCount}");
            Console.WriteLine();

            Console.WriteLine($"Discard: {lambdaData.TotalDiscardsCount}");
            Console.WriteLine($"Other underscore: {lambdaData.TotalOtherUnderscoresCount}");
        }

        //private static readonly string BasePath = @"C:\Users\Svick\AppData\Local\Temp\UnderscoreLambdasGithub\";
        private static readonly string BasePath = @"C:\Temp\UnderscoreLambdasGithub\";

        private static async Task SendData(ITargetBlock<string> targetBlock)
        {
            foreach (var repo in Repos ?? new string[0])
            {
                await targetBlock.SendAsync(repo);
            }

            targetBlock.Complete();
        }

        private static Data Parse(string file)
        {
            //Console.WriteLine($"Parsing {file.Substring(BasePath.Length)}.");

            var data = new Data();

            var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(File.OpenRead(file))).WithFilePath(file.Substring(BasePath.Length));

            // that many syntax errors means it's not a C# file
            if (syntaxTree.GetDiagnostics().Count() > 100)
                return data;

            var semanticModel = CSharpCompilation.Create(null)
                .AddSyntaxTrees(syntaxTree)
                .GetSemanticModel(syntaxTree);

            var handledUnderscores = new HashSet<ISymbol>();

            var root = syntaxTree.GetCompilationUnitRoot();
            var lambdas = root.DescendantNodes().OfType<LambdaExpressionSyntax>();

            foreach (var lambda in lambdas)
            {
                var names = lambda.DescendantNodes().OfType<NameSyntax>().Select(name => semanticModel.GetSymbolInfo(name).Symbol).Where(s => s != null).ToList();

                if (lambda is SimpleLambdaExpressionSyntax simpleLambda)
                {
                    var paramaterSymbol = semanticModel.GetDeclaredSymbol(simpleLambda.Parameter);

                    SingleLambda(data, names, paramaterSymbol);

                    handledUnderscores.Add(paramaterSymbol);
                }

                if (lambda is ParenthesizedLambdaExpressionSyntax complexLambda)
                {
                    var parameterSymbols = complexLambda.ParameterList.Parameters
                        .Select(param => semanticModel.GetDeclaredSymbol(param))
                        .ToList();

                    if (parameterSymbols.Count == 1)
                    {
                        SingleLambda(data, names, parameterSymbols.Single());
                    }
                    else
                    {
                        MultiLambda(data, names, parameterSymbols);
                    }

                    handledUnderscores.UnionWith(parameterSymbols);
                }
            }

            foreach (var name in root.DescendantNodes().OfType<NameSyntax>())
            {
                if (name.ToString() != name.ToString().Trim())
                    throw new Exception();

                if (name.ToString() == "_")
                {
                    var symbol = semanticModel.GetSymbolInfo(name).Symbol;

                    if (symbol is IDiscardSymbol)
                    {
                        data.Discard();
                    }
                    else if (handledUnderscores.Add(symbol))
                    {
                        Print(name.Parent);

                        data.OtherUnderscore();
                    }
                }
            }

            return data;
        }

        private static void Print(SyntaxNode node)
        {
            var ancestors = node.AncestorsAndSelf();

            var toPrint = ancestors.OfType<StatementSyntax>().FirstOrDefault() ??
                          ancestors.OfType<ExpressionSyntax>().LastOrDefault() ?? node;

            Console.WriteLine($"{node.SyntaxTree.FilePath}: {toPrint}");

            File.AppendAllLines(@"C:\temp\UnderscoreLambdasGithub\other.txt", new[] {toPrint.ToString()});
        }

        private static void MultiLambda(Data lambdaData, List<ISymbol> names, List<IParameterSymbol> parameterSymbols)
        {
            lambdaData.MultiLambda();

            var underscoreParameter = parameterSymbols.FirstOrDefault(s => s.Name == "_");
            if (underscoreParameter != null)
            {
                lambdaData.MultiLambdaUnderscore();

                if (!names.Contains(underscoreParameter))
                {
                    lambdaData.MultiLambdaUnderscoreUnused();
                }
            }
        }

        private static void SingleLambda(Data lambdaData, List<ISymbol> names, IParameterSymbol parameterSymbol)
        {
            lambdaData.SingleLambda();

            if (parameterSymbol.Name == "_")
            {
                lambdaData.SingleLambdaUnderscore();

                if (!names.Contains(parameterSymbol))
                {
                    lambdaData.SingleLambdaUnderscoreUnused();
                }
            }
        }

        private static IEnumerable<string> GetFiles(string path)
        {
            // this can happen when clone fails
            if (!Directory.Exists(path))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories);
        }

        private static int i;

        private static string Clone(string repo)
        {
            Console.WriteLine($"{Interlocked.Increment(ref i)}/{Repos.Length} {repo}");

            string gitUrl = $"https://github.com/{repo}.git";
            string path = Path.Combine(BasePath, repo.Replace('/', ' '));

            if (Directory.Exists(path))
                Console.WriteLine($"Directory for {repo} already exists.");
            else
                Process.Start("git", $"clone {gitUrl} \"{path}\" --depth 1").WaitForExit();

            return path;
        }

        private static string[] Repos;

        static IEnumerable<string> GetRepos(int pages)
        {
            var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd("gsvick at gmail.com");

            for (int i = 1; i <= pages; i++)
            {
                var jsonString = client.GetStringAsync(
                    $"https://api.github.com/search/repositories?q=language:csharp+stars:%3C=100&sort=updated&per_page=100&page={i}").Result;

                dynamic data = JsonConvert.DeserializeObject(jsonString);
                foreach (var repo in data.items)
                {
                    yield return repo.full_name;
                }
            }
        }
    }

    class Data
    {
        public int TotalSingleLambdasCount { get; private set; }
        public void SingleLambda() => TotalSingleLambdasCount++;

        public int TotalSingleLambdaUnderscoreCount { get; private set; }
        public void SingleLambdaUnderscore() => TotalSingleLambdaUnderscoreCount++;

        public int TotalSingleLambdaUnderscoreUnusedCount { get; private set; }
        public void SingleLambdaUnderscoreUnused() => TotalSingleLambdaUnderscoreUnusedCount++;

        public int TotalMultiLambdasCount { get; private set; }
        public void MultiLambda() => TotalMultiLambdasCount++;

        public int TotalMultiLambdaUnderscoreCount { get; private set; }
        public void MultiLambdaUnderscore() => TotalMultiLambdaUnderscoreCount++;

        public int TotalMultiLambdaUnderscoreUnusedCount { get; private set; }
        public void MultiLambdaUnderscoreUnused() => TotalMultiLambdaUnderscoreUnusedCount++;

        public int TotalOtherUnderscoresCount { get; private set; }
        public void OtherUnderscore() => TotalOtherUnderscoresCount++;

        public int TotalDiscardsCount { get; private set; }
        public void Discard() => TotalDiscardsCount++;

        public void Add(Data other)
        {
            this.TotalSingleLambdasCount += other.TotalSingleLambdasCount;
            this.TotalSingleLambdaUnderscoreCount += other.TotalSingleLambdaUnderscoreCount;
            this.TotalSingleLambdaUnderscoreUnusedCount += other.TotalSingleLambdaUnderscoreUnusedCount;

            this.TotalMultiLambdasCount += other.TotalMultiLambdasCount;
            this.TotalMultiLambdaUnderscoreCount += other.TotalMultiLambdaUnderscoreCount;
            this.TotalMultiLambdaUnderscoreUnusedCount += other.TotalMultiLambdaUnderscoreUnusedCount;

            this.TotalOtherUnderscoresCount += other.TotalOtherUnderscoresCount;
            this.TotalDiscardsCount += other.TotalDiscardsCount;
        }
    }
}
