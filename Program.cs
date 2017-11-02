using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
        private const int ReposCount = 10000;

        public static void Main()
        {
            int i = 0;
            var repos = GetRepos().Distinct().Take(ReposCount).Do(
                _ =>
                {
                    if (++i % 100 == 0)
                        Console.Error.WriteLine($"Repos: {i}");
                });

            var commonOptions = new ExecutionDataflowBlockOptions { BoundedCapacity = 1 };
            var parallelOptions = new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = 4,
                MaxDegreeOfParallelism = 4,
                EnsureOrdered = false
            };

            var cloneBlock = new TransformBlock<string, string>(repo => Clone(repo), parallelOptions);

            var getFilesBlock = new TransformManyBlock<string, (string, IDisposable)>(path => GetFiles(path), commonOptions);

            var parseBlock = new TransformBlock<(string, IDisposable), Data>(tuple => Parse(tuple.Item1, tuple.Item2), commonOptions);

            Data dataSummary = new Data();
            var summarizeBlock = new ActionBlock<Data>(ld => dataSummary.Add(ld));

            var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

            repos.Subscribe(cloneBlock.AsObserver());
            cloneBlock.LinkTo(getFilesBlock, linkOptions);
            getFilesBlock.LinkTo(parseBlock, linkOptions);
            parseBlock.LinkTo(summarizeBlock, linkOptions);

            //getFilesBlock.SendAsync(BasePath).Wait();
            //cloneBlock.Complete();

            summarizeBlock.Completion.Wait();

            Print(dataSummary);
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

        private static readonly string BasePath = Path.Combine(Environment.CurrentDirectory, "../UnderscoreLambdasGithubData");

        private static Data Parse(string file, IDisposable disposable)
        {
            //Console.WriteLine($"Parsing {file.Substring(BasePath.Length)}.");

            var data = new Data();

            SyntaxTree syntaxTree;

            using (var fileStream = File.OpenRead(file))
            {
                syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(fileStream))
                    .WithFilePath(file.Substring(BasePath.Length));
            }

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

            disposable.Dispose();

            return data;
        }

        private static void Print(SyntaxNode node)
        {
            var ancestors = node.AncestorsAndSelf();

            var toPrint = ancestors.OfType<StatementSyntax>().FirstOrDefault() ??
                          ancestors.OfType<ExpressionSyntax>().LastOrDefault() ?? node;

            Console.WriteLine(node.SyntaxTree.FilePath);
            Console.WriteLine(toPrint.ToFullString());
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

        private static IEnumerable<(string, IDisposable)> GetFiles(string path)
        {
            // this can happen when clone fails
            if (!Directory.Exists(path))
            {
                return Enumerable.Empty<(string, IDisposable)>();
            }

            // https://stackoverflow.com/a/8714329/41071
            void ForceDeleteDirectory()
            {
                var directory = new DirectoryInfo(path);

                foreach (var info in directory.GetFileSystemInfos("*", SearchOption.AllDirectories))
                {
                    info.Attributes = FileAttributes.Normal;
                }

                directory.Refresh();

                directory.Delete(true);
            }

            var disposable = new RefCountDisposable(Disposable.Create(ForceDeleteDirectory));

            // ToList is required, so that all inner disposables are created before the outer is disposed
            var files = Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                .Select(f => (f, disposable.GetDisposable()))
                .ToList();

            disposable.Dispose();

            return files;
        }

        private static int i;

        private static string Clone(string repo)
        {
            Console.Error.WriteLine($"{Interlocked.Increment(ref i)}/{ReposCount} {repo}");

            string gitUrl = $"https://github.com/{repo}.git";
            string path = Path.Combine(BasePath, repo.Replace('/', ' '));

            if (Directory.Exists(path))
                Console.Error.WriteLine($"Directory for {repo} already exists.");
            else
                Process.Start(
                    new ProcessStartInfo("git", $"clone {gitUrl} \"{path}\" --depth 1")
                    {
                        RedirectStandardError = true
                    }).WaitForExit();

            return path;
        }

        static IObservable<string> GetRepos()
        {
            var repoNamesObservable = Observable.Create<IObservable<string>>(
                async (o, ct) =>
                {
                    DateTime start = DateTime.UtcNow;

                    while (true)
                    {
                        var (repoNames, nextStartTask) = GetRepos(start);

                        o.OnNext(repoNames);

                        if (ct.IsCancellationRequested)
                        {
                            Console.Error.WriteLine("Got enough sets of repos, cancelling.");
                            ct.ThrowIfCancellationRequested();
                        }

                        start = await nextStartTask;
                    }
                });

            return repoNamesObservable.Concat();
        }

        static (IObservable<string> repoNames, Task<DateTime> nextStart) GetRepos(DateTime start)
        {
            var nextStartTcs = new TaskCompletionSource<DateTime>();

            var repoNamesObservable = Observable.Create<string>(
                async (o, ct) =>
                {
                    var client = new HttpClient();

                    client.DefaultRequestHeaders.UserAgent.ParseAdd("gsvick at gmail.com");

                    DateTime nextStart = start;

                    for (int i = 1; i <= 10; i++)
                    {
                        async Task<dynamic> GetData()
                        {
                            while (true)
                            {
                                if (ct.IsCancellationRequested)
                                {
                                    Console.Error.WriteLine("Got enough repos, cancelling.");
                                    ct.ThrowIfCancellationRequested();
                                }

                                string url =
                                    $"https://api.github.com/search/repositories?q=language:csharp+stars:%3C=100+pushed:%3C={start:s}&sort=updated&per_page=100&page={i}";

                                Console.Error.WriteLine($"start: {start:s}; page: {i}");

                                HttpResponseMessage response;
                                try
                                {
                                    response = await client.GetAsync(url, ct);
                                }
                                catch (OperationCanceledException)
                                {
                                    Console.Error.WriteLine("Timeout, retrying.");
                                    continue;
                                }

                                if (response.StatusCode == HttpStatusCode.Forbidden)
                                {
                                    var waitUntilTimestamp = response.Headers.GetValues("X-RateLimit-Reset").Single();

                                    var waitUntil = DateTimeOffset.FromUnixTimeSeconds(long.Parse(waitUntilTimestamp));

                                    var waitTime = waitUntil - DateTimeOffset.UtcNow;

                                    Console.Error.WriteLine($"403, waiting for {waitTime.TotalSeconds:F2} s.");

                                    if (waitTime > TimeSpan.Zero)
                                        await Task.Delay(waitTime, ct);

                                    continue;
                                }

                                var jsonString = await response.Content.ReadAsStringAsync();

                                response.EnsureSuccessStatusCode();

                                return JsonConvert.DeserializeObject(jsonString);
                            }
                        }

                        var data = await GetData();

                        bool incomplete = data.incomplete_results;

                        foreach (var repo in data.items)
                        {
                            string repoName = repo.full_name;
                            o.OnNext(repoName);

                            // if it's incomplete, we got "random" repos, so don't change nextStart
                            // if it's complete, nextStart has to move
                            if (!incomplete)
                            {
                                DateTime pushed = repo.pushed_at;
                                nextStart = pushed;
                            }
                        }

                        if (!incomplete)
                            Console.Error.WriteLine($"Got complete data, moving start to {nextStart:s}.");
                    }

                    o.OnCompleted();

                    nextStartTcs.SetResult(nextStart);
                });

            return (repoNamesObservable, nextStartTcs.Task);
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
