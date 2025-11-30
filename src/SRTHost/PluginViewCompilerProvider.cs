using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.Extensions.Logging;

namespace SRTHost
{
	public class PluginViewCompilerProvider : IViewCompilerProvider
	{
		public PluginViewCompilerProvider(ApplicationPartManager applicationPartManager, ILoggerFactory loggerFactory)
		{
			this.Compiler = new PluginViewCompiler(applicationPartManager, loggerFactory);
		}

		protected IViewCompiler Compiler { get; }

		public IViewCompiler GetCompiler()
		{
			return this.Compiler;
		}
	}
}
