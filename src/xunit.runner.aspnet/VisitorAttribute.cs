using System;

namespace xunit.runner.aspnet
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class VisitorAttribute : Attribute
    {
        public VisitorAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string[] EnvironmentVariables { get; set; }
    }
}