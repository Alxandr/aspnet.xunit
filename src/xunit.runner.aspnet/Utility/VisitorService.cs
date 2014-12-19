using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.Framework.Runtime;

namespace xunit.runner.aspnet.Utility
{
    public class VisitorService
    {
        readonly ImmutableDictionary<string, Type> _visitorTypes;
        readonly ImmutableDictionary<string, Type> _envKeys;
        readonly Lazy<Type> _detected;

        public VisitorService(ILibraryManager libraryManager)
        {
            var visitorTypes = ImmutableDictionary.CreateBuilder<string, Type>();
            var envKeys = ImmutableDictionary.CreateBuilder<string, Type>();

            foreach(var lib in libraryManager.GetReferencingLibraries("xunit.runner.aspnet"))
            {
                foreach(var name in lib.LoadableAssemblies)
                {
                    var asm = Assembly.Load(name);
                    foreach (var type in asm.ExportedTypes)
                    {
                        var attr = type.GetTypeInfo().GetCustomAttribute<VisitorAttribute>();
                        if (attr != null)
                        {
                            visitorTypes.Add(attr.Name, type);
                            if (attr.EnvironmentVariables != null)
                            {
                                foreach (var e in attr.EnvironmentVariables)
                                {
                                    envKeys.Add(e, type);
                                }
                            }
                        }
                    }
                }
            }

            _visitorTypes = visitorTypes.ToImmutable();
            _envKeys = envKeys.ToImmutable();

            _detected = new Lazy<Type>(() =>
            {
                foreach (var kvp in _envKeys)
                {
                    if (Environment.GetEnvironmentVariable(kvp.Key) != null)
                        return kvp.Value;
                }

                return null;
            });
        }

        public Type GetVisitorType(string name)
        {
            Type result;
            return _visitorTypes.TryGetValue(name, out result) ? result : null;
        }

        public Type Recomended => _detected.Value;
    }
}