using Microsoft.Extensions.DependencyInjection;
using SRTHost.LoggerImplementations;
using System;

namespace Microsoft.Extensions.Logging
{
    public static class FileLoggerExtensions
    {
        public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string fileName, Action<FileLoggerOptions> configure = default)
        {
            builder.Services.Add(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>(
                (IServiceProvider serviceProvider) =>
                {
                    FileLoggerOptions options = new FileLoggerOptions();
                    if (configure != null)
                        configure(options);
                    return new FileLoggerProvider(fileName, options);
                }
            ));
            return builder;
        }
    }
}
