using System;

namespace SRTPluginBase
{
    public interface IPluginProducer : IPlugin, IEquatable<IPluginProducer>
    {
        /// <summary>
        /// Instructs the producer plugin to retrieve data and return a structure representing the data retrieved.
        /// </summary>
        /// <returns>Plugin-specific data structure.</returns>
        object PullData();

        /// <summary>
        /// Whether the producer is available or not.
        /// </summary>
        bool Available { get; }

        public new bool Equals(IPluginProducer? other) => TypeName == other?.TypeName && Info.Name == other?.Info.Name;

        public new bool Equals(object? obj) => Equals(obj as IPluginProducer);

        public new int GetHashCode() => HashCode.Combine(this as IPlugin, Available);
    }
}
