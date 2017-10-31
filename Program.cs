using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
    public class Program
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

            getFilesBlock.SendAsync(BasePath).Wait();

            var parseBlock = new TransformBlock<string, Data>(file => Parse(file), commonOptions);

            Data dataSummary = new Data();
            var summarizeBlock = new ActionBlock<Data>(ld => dataSummary.Add(ld));

            var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

            cloneBlock.LinkTo(getFilesBlock, linkOptions);
            getFilesBlock.LinkTo(parseBlock, linkOptions);
            parseBlock.LinkTo(summarizeBlock, linkOptions);

            var sendDataTask = SendData(cloneBlock);

            summarizeBlock.Completion.Wait();

            sendDataTask.Wait();

            Print(dataSummary);

            Console.WriteLine();
        }

        private static void Print(Data data)
        {
            Print(data.Joins);
        }

        private static void Print(Dictionary<string, int> dictionary)
        {
            var data = dictionary.OrderByDescending(kvp => kvp.Value).Take(10);

            Console.WriteLine($"Total: {dictionary.Values.Sum()}");

            foreach (var kvp in data)
            {
                Console.WriteLine($"'{kvp.Key}'\t{kvp.Value}");
            }

            Console.WriteLine();

            Console.WriteLine("| | count |");
            Console.WriteLine("|---|---|");

            Console.WriteLine($"| total | {dictionary.Values.Sum()} |");

            foreach (var kvp in data)
            {
                Console.WriteLine($"| \"{Format(kvp.Key)}\" | {kvp.Value} |");
            }
        }

        private static string Format(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

        //private static readonly string BasePath = @"C:\Users\Svick\AppData\Local\Temp\UnderscoreLambdasGithub\";
        private static readonly string BasePath = @"E:\Temp\UnderscoreLambdasGithub\";

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

            var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(File.OpenRead(file)));
            var semanticModel = CSharpCompilation.Create(null)
                .AddSyntaxTrees(syntaxTree)
                .AddReferences(MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location))
                .GetSemanticModel(syntaxTree);

            var invocations = syntaxTree.GetCompilationUnitRoot().DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                if (invocation.ToString().Contains("Join"))
                {
                    //Console.WriteLine(file.Substring(BasePath.Length) + ':');
                    Console.WriteLine(invocation);
                }

                var symbolInfo = semanticModel.GetSymbolInfo(invocation.Expression);
                IEnumerable<ISymbol> symbols = symbolInfo.CandidateSymbols;
                if (symbolInfo.Symbol != null)
                    symbols = symbols.Prepend(symbolInfo.Symbol);

                if (!symbols.Any(s => s.ContainingType?.SpecialType == SpecialType.System_String && s.Name == "Join"))
                    continue;

                var firstParameterLiteral = invocation.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax;

                if (firstParameterLiteral == null)
                    continue;

                var firstParameterValue = firstParameterLiteral.Token.ValueText;

                Console.WriteLine($"Found '{firstParameterValue}'");
                data.Join(firstParameterValue);
            }

            return data;
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
            Console.WriteLine($"{Interlocked.Increment(ref i)} {repo}");

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
        public Dictionary<string, int> Joins { get; } = new Dictionary<string, int>();

        public static void Increment<T>(Dictionary<T, int> dictionary, T key, int added = 1)
        {
            int value;
            dictionary.TryGetValue(key, out value);
            dictionary[key] = value + added;
        }

        public void Join(string separator) => Increment(Joins, separator);

        private static void Add(Dictionary<string, int> thisDictionary, Dictionary<string, int> otherDictionary)
        {
            foreach (var kvp in otherDictionary)
            {
                Increment(thisDictionary, kvp.Key, kvp.Value);
            }
        }

        public void Add(Data other)
        {
            Add(this.Joins, other.Joins);
        }
    }
}
