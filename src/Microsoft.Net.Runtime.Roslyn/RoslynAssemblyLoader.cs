﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Net.Runtime.Loader;
using Microsoft.Net.Runtime.FileSystem;
using Microsoft.Net.Runtime.Services;

#if NET45 // TODO: Temporary due to CoreCLR and Desktop Roslyn being out of sync
using EmitResult = Microsoft.CodeAnalysis.Emit.CommonEmitResult;
#endif

namespace Microsoft.Net.Runtime.Roslyn
{
    public class RoslynAssemblyLoader : IAssemblyLoader, IMetadataReferenceProvider
    {
        private readonly Dictionary<string, CompilationContext> _compilationCache = new Dictionary<string, CompilationContext>();

        private readonly IRoslynCompiler _compiler;
        private readonly IAssemblyLoaderEngine _loaderEngine;
        private readonly IProjectResolver _projectResolver;
        private readonly IResourceProvider _resourceProvider;

        public RoslynAssemblyLoader(IAssemblyLoaderEngine loaderEngine,
                                    IFileWatcher watcher,
                                    IProjectResolver projectResolver,
                                    IDependencyExporter dependencyExporter,
                                    IGlobalAssemblyCache globalAssemblyCache)
        {
            _loaderEngine = loaderEngine;
            _projectResolver = projectResolver;

            var frameworkResolver = new FrameworkReferenceResolver(globalAssemblyCache);

            var resxProvider = new ResxResourceProvider();
            var embeddedResourceProvider = new EmbeddedResourceProvider();

            _resourceProvider = new CompositeResourceProvider(new IResourceProvider[] { resxProvider, embeddedResourceProvider });
            _compiler = new RoslynCompiler(projectResolver,
                                           watcher,
                                           frameworkResolver,
                                           dependencyExporter);
        }

        public AssemblyLoadResult Load(LoadContext loadContext)
        {
            var compilationContext = GetCompilationContext(loadContext.AssemblyName, loadContext.TargetFramework);

            if (compilationContext == null)
            {
                return null;
            }

            var project = compilationContext.Project;
            var path = project.ProjectDirectory;
            var name = project.Name;

            var resources = _resourceProvider.GetResources(project);

            foreach (var reference in compilationContext.AssemblyNeutralReferences)
            {
                resources.Add(new ResourceDescription(reference.Name + ".dll",
                                                      () => reference.OutputStream,
                                                      isPublic: true));
            }

            return CompileInMemory(name, compilationContext, resources);
        }

        public IEnumerable<object> GetReferences(string name, FrameworkName targetFramework)
        {
            var compilationContext = GetCompilationContext(name, targetFramework);

            if (compilationContext == null)
            {
                return Enumerable.Empty<MetadataReference>();
            }
            var thisProject = compilationContext.Compilation.ToMetadataReference();

            return new[] { thisProject }.Concat(compilationContext.Compilation.References);
        }

        private CompilationContext GetCompilationContext(string name, FrameworkName targetFramework)
        {
            CompilationContext compilationContext;
            if (_compilationCache.TryGetValue(name, out compilationContext))
            {
                return compilationContext;
            }

            var context = _compiler.CompileProject(name, targetFramework);

            if (context == null)
            {
                return null;
            }

            CacheCompilation(context);

            return context;
        }

        private void CacheCompilation(CompilationContext context)
        {
            _compilationCache[context.Project.Name] = context;

            foreach (var ctx in context.ProjectReferences)
            {
                CacheCompilation(ctx);
            }
        }

        private AssemblyLoadResult CompileInMemory(string name, CompilationContext compilationContext, IEnumerable<ResourceDescription> resources)
        {
            using (var pdbStream = new MemoryStream())
            using (var assemblyStream = new MemoryStream())
            {
#if NET45
                EmitResult result = compilationContext.Compilation.Emit(assemblyStream, pdbStream: pdbStream, manifestResources: resources);
#else
                EmitResult result = compilationContext.Compilation.Emit(assemblyStream);
#endif

                if (!result.Success)
                {
                    return ReportCompilationError(
                        compilationContext.Diagnostics.Where(IsError).Concat(result.Diagnostics));
                }

                var errors = compilationContext.Diagnostics.Where(IsError);
                if (errors.Any())
                {
                    return ReportCompilationError(errors);
                }

                var assemblyBytes = assemblyStream.ToArray();
                byte[] pdbBytes = null;
#if NET45
                pdbBytes = pdbStream.ToArray();
#endif

                var assembly = _loaderEngine.LoadBytes(assemblyBytes, pdbBytes);

                return new AssemblyLoadResult(assembly);
            }
        }

        private static AssemblyLoadResult ReportCompilationError(IEnumerable<Diagnostic> results)
        {
            return new AssemblyLoadResult(GetErrors(results));
        }

        private static IList<string> GetErrors(IEnumerable<Diagnostic> diagnostis)
        {
#if NET45 // TODO: Temporary due to CoreCLR and Desktop Roslyn being out of sync
            var formatter = DiagnosticFormatter.Instance;
#else
            var formatter = new DiagnosticFormatter();
#endif
            return diagnostis.Select(d => formatter.Format(d)).ToList();
        }

        private static bool IsError(Diagnostic diagnostic)
        {
            return diagnostic.Severity == DiagnosticSeverity.Error || diagnostic.IsWarningAsError;
        }
    }
}
