using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Linq;

namespace SRTHost
{
	public class PluginViewCompiler : IViewCompiler
	{
		protected ApplicationPartManager ApplicationPartManager { get; }
		protected ILogger<PluginViewCompiler> Logger { get; }
		protected IDictionary<string, CancellationTokenSource> CancellationTokenSources { get; }
		protected IDictionary<string, string> NormalizedPathCache { get; }
		protected IDictionary<string, CompiledViewDescriptor> CompiledViews { get; private set; }

		public static PluginViewCompiler? Current { get; private set; }

		public PluginViewCompiler(ApplicationPartManager applicationPartManager, ILoggerFactory loggerFactory)
		{
			this.ApplicationPartManager = applicationPartManager;
			this.Logger = loggerFactory.CreateLogger<PluginViewCompiler>();
			this.CancellationTokenSources = new Dictionary<string, CancellationTokenSource>();
			this.NormalizedPathCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

			ViewsFeature feature = new ViewsFeature();
			this.ApplicationPartManager.PopulateFeature(feature);
			this.CompiledViews = new Dictionary<string, CompiledViewDescriptor>(feature.ViewDescriptors.Count, StringComparer.OrdinalIgnoreCase);
			foreach (CompiledViewDescriptor compiledView in feature.ViewDescriptors)
			{
				if (this.CompiledViews.ContainsKey(compiledView.RelativePath))
					continue;
				this.CompiledViews.Add(compiledView.RelativePath, compiledView);
			};

			Current = this;
		}

		public void LoadModuleCompiledViews(Assembly moduleAssembly)
		{
			if (moduleAssembly is null)
				throw new ArgumentNullException(nameof(moduleAssembly));
			CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
			this.CancellationTokenSources.Add(moduleAssembly.FullName ?? string.Empty, cancellationTokenSource);
			ViewsFeature feature = new ViewsFeature();
			foreach (ApplicationPart applicationPart in CompiledRazorAssemblyApplicationPartFactory.GetDefaultApplicationParts(moduleAssembly))
				this.ApplicationPartManager.ApplicationParts.Add(applicationPart);
			this.ApplicationPartManager.PopulateFeature(feature);
			foreach (CompiledViewDescriptor compiledView in feature.ViewDescriptors.Where(v => v.Type?.Assembly == moduleAssembly))
			{
				if (!this.CompiledViews.ContainsKey(compiledView.RelativePath))
				{
					compiledView.ExpirationTokens = new List<IChangeToken>() { new CancellationChangeToken(cancellationTokenSource.Token) };
					this.CompiledViews.Add(compiledView.RelativePath, compiledView);
				}
			}
		}

		public void UnloadModuleCompiledViews(Assembly moduleAssembly)
		{
			if (moduleAssembly is null)
				throw new ArgumentNullException(nameof(moduleAssembly));
			foreach (KeyValuePair<string, CompiledViewDescriptor> entry in this.CompiledViews.Where(kvp => kvp.Value.Type?.Assembly == moduleAssembly))
			{
				this.CompiledViews.Remove(entry.Key);
			}
			foreach (ApplicationPart applicationPart in CompiledRazorAssemblyApplicationPartFactory.GetDefaultApplicationParts(moduleAssembly))
				this.ApplicationPartManager.ApplicationParts.Remove(applicationPart);
			if (this.CancellationTokenSources.TryGetValue(moduleAssembly.FullName ?? string.Empty, out CancellationTokenSource? cancellationTokenSource))
			{
				cancellationTokenSource!.Cancel();
				this.CancellationTokenSources.Remove(moduleAssembly.FullName ?? string.Empty);
			}
		}

		public async Task<CompiledViewDescriptor> CompileAsync(string relativePath)
		{
			if (relativePath is null)
				throw new ArgumentNullException(nameof(relativePath));
			if (this.CompiledViews.TryGetValue(relativePath, out CompiledViewDescriptor? cachedResult))
				return cachedResult!;
			string normalizedPath = this.GetNormalizedPath(relativePath);
			if (this.CompiledViews.TryGetValue(normalizedPath, out cachedResult))
				return cachedResult;
			return await Task.FromResult(new CompiledViewDescriptor()
			{
				RelativePath = normalizedPath,
				ExpirationTokens = Array.Empty<IChangeToken>(),
			});
		}

		protected string GetNormalizedPath(string relativePath)
		{
			if (relativePath.Length == 0)
				return relativePath;
			if (!this.NormalizedPathCache.TryGetValue(relativePath, out var normalizedPath))
			{
				normalizedPath = this.NormalizePath(relativePath);
				this.NormalizedPathCache[relativePath] = normalizedPath;
			}
			return normalizedPath;
		}

		protected string NormalizePath(string path)
		{
			bool addLeadingSlash = path[0] != '\\' && path[0] != '/';
			bool transformSlashes = path.IndexOf('\\') != -1;
			if (!addLeadingSlash && !transformSlashes)
				return path;
			int length = path.Length;
			if (addLeadingSlash)
				length++;
			return string.Create(length, (path, addLeadingSlash), (span, tuple) =>
			{
				var (pathValue, addLeadingSlashValue) = tuple;
				int spanIndex = 0;
				if (addLeadingSlashValue)
					span[spanIndex++] = '/';
				foreach (var ch in pathValue)
				{
					span[spanIndex++] = ch == '\\' ? '/' : ch;
				}
			});
		}
	}
}