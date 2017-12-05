// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NuGet.Frameworks;
using NuGet.Versioning;
using RepoTools.BuildGraph;
using RepoTasks.ProjectModel;
using RepoTasks.Utilities;
using RepoTasks.CodeGen;
using NuGet.Packaging.Core;

namespace RepoTasks
{
    public class GenerateSubmoduleProjects : Task, ICancelableTask
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Repositories that we are building new versions of.
        /// </summary>
        [Required]
        public ITaskItem[] Solutions { get; set; }

        [Required]
        public ITaskItem[] Repositories { get; set; }

        [Required]
        public string RepositoryRoot { get; set; }

        [Required]
        public string Properties { get; set; }

        public void Cancel()
        {
            _cts.Cancel();
        }

        public override bool Execute()
        {
            var factory = new SolutionInfoFactory(Log, BuildEngine5);
            var props = MSBuildListSplitter.GetNamedProperties(Properties);

            Log.LogMessage(MessageImportance.High, $"Beginning cross-repo analysis on {Solutions.Length} solutions. Hang tight...");

            if (!props.TryGetValue("Configuration", out var defaultConfig))
            {
                defaultConfig = "Debug";
            }

            var solutions = factory.Create(Solutions, props, defaultConfig, _cts.Token).OrderBy(f => f.Directory).ToList();
            Log.LogMessage($"Found {solutions.Count} and {solutions.Sum(p => p.Projects.Count)} projects");

            if (_cts.IsCancellationRequested)
            {
                return false;
            }

            var repoGraph = new AdjacencyMatrix(solutions.Count);
            var packageToProjectMap = new Dictionary<PackageIdentity, ProjectInfo>();

            for (var i = 0; i < solutions.Count; i++)
            {
                var sln = repoGraph[i] = solutions[i];

                foreach (var proj in sln.Projects)
                {
                    if (!proj.IsPackable
                        || proj.FullPath.Contains("samples")
                        || proj.FullPath.Contains("tools/Microsoft.VisualStudio.Web.CodeGeneration.Design"))
                    {
                        continue;
                    }

                    var id = new PackageIdentity(proj.PackageId, new NuGetVersion(proj.PackageVersion));

                    if (packageToProjectMap.TryGetValue(id, out var otherProj))
                    {
                        Log.LogError($"Both {proj.FullPath} and {otherProj.FullPath} produce {id}");
                        continue;
                    }

                    packageToProjectMap.Add(id, proj);
                }
            }

            if (Log.HasLoggedErrors)
            {
                return false;
            }

            for (var i = 0; i < solutions.Count; i++)
            {
                var sln = repoGraph[i];

                var deps = from proj in sln.Projects
                           from tfm in proj.Frameworks
                           from dep in tfm.Dependencies.Values
                           select dep;

                foreach (var dep in deps)
                {
                    if (packageToProjectMap.TryGetValue(new PackageIdentity(dep.Id, new NuGetVersion(dep.Version)), out var target))
                    {
                        var j = repoGraph.FindIndex(target.SolutionInfo);
                        repoGraph.SetLink(i, j);
                    }
                }
            }

            CreateDgml(repoGraph);
            CreateProjects(repoGraph);

            return !Log.HasLoggedErrors;
        }

        private void CreateProjects(AdjacencyMatrix slnGraph)
        {
            var root = Path.Combine(RepositoryRoot, "src", "Managed");
            Directory.CreateDirectory(root);

            var repos = Repositories.ToDictionary(i => i.ItemSpec, i => i, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < slnGraph.Count; i++)
            {
                var sln = slnGraph[i];
                var dest = Path.Combine(root, Path.GetFileNameWithoutExtension(sln.FullPath) + ".repoproj");
                var repoName = Path.GetFileName(sln.Directory);
                var repo = repos[repoName];
                var repoRootRelative = Path.GetRelativePath(Path.GetDirectoryName(dest), sln.Directory);
                var repoproj = new RepositoryProject(repoRootRelative);

                bool.TryParse(repo.GetMetadata("UseBuildCache"), out var useBuildCache);
                repoproj.AddProperty("UseBuildCache", useBuildCache.ToString());

                for (var j = 0; j < slnGraph.Count; j++)
                {
                    if (j == i) continue;
                    if (slnGraph.HasLink(i, j))
                    {
                        var target = slnGraph[j];
                        var targetSlnProj = Path.GetFileNameWithoutExtension(target.FullPath) + ".repoproj";

                        var targetRepoName = Path.GetFileName(target.Directory);
                        var targetRepo = repos[targetRepoName];

                        if (useBuildCache && bool.TryParse(targetRepo.GetMetadata("UseBuildCache"), out var targetUseBuildCache) && !targetUseBuildCache)
                        {
                            Log.LogError($"{repoName} cannot depend on {targetRepoName}. Repos with UseBuildCache true cannot depend on repos with UseBuildCache false. Update the configuration in submodule.props.");
                        }

                        repoproj.AddProjectReference(targetSlnProj);
                    }
                }

                repoproj.Save(dest);
            }
        }

        private void CreateDgml(AdjacencyMatrix repoGraph)
        {
            var dgml = new DirectedGraphXml();

            for (var i = 0; i < repoGraph.Count; i++)
            {
                var node = repoGraph[i];
                var nodeName = Path.GetFileName(node.Directory);
                dgml.AddNode(nodeName);

                for (var j = 0; j < repoGraph.Count; j++)
                {
                    if (j == i) continue;
                    if (repoGraph.HasLink(i, j))
                    {
                        var target = repoGraph[j];
                        var targetName = Path.GetFileName(target.Directory);
                        dgml.AddLink(nodeName, targetName);
                    }
                }
            }

            dgml.Save(Path.Combine(RepositoryRoot, "modules", "SubmoduleGraph.dgml"));
        }

        private class AdjacencyMatrix
        {
            private readonly bool[,] _matrix;
            private readonly SolutionInfo[] _items;

            public AdjacencyMatrix(int size)
            {
                _matrix = new bool[size, size];
                _items = new SolutionInfo[size];
                Count = size;
            }

            public SolutionInfo this[int idx]
            {
                get => _items[idx];
                set => _items[idx] = value;
            }

            public int FindIndex(SolutionInfo item)
            {
                return Array.FindIndex(_items, t => t.Equals(item));
            }

            public int Count { get; }

            public bool HasLink(int source, int target) => _matrix[source, target];

            public void SetLink(int source, int target)
            {
                _matrix[source, target] = true;
            }
        }
    }
}
