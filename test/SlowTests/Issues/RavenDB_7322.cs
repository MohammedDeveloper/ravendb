﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FastTests;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_7322 : NoDisposalNeeded
    {
        private readonly HashSet<Assembly> _assemblies = new HashSet<Assembly>();

        [Fact]
        public void TestClassesShouldNotInheritFromOtherTestClassesToNotMultiplyTests()
        {
            var classes = from assembly in GetAssemblies(typeof(RavenDB_7322).GetTypeInfo().Assembly)
                          from test in assembly.GetTypes()
                          where test.GetMethods().Any(x => x.GetCustomAttributes(typeof(FactAttribute), true).Count() != 0 || x.GetCustomAttributes(typeof(TheoryAttribute), true).Count() != 0)
                          select test;

            var dictionary = classes.ToDictionary(x => x, x => x.GetTypeInfo().BaseType);

            var sb = new StringBuilder();
            foreach (var kvp in dictionary)
            {
                if (dictionary.TryGetValue(kvp.Value, out var _) == false)
                    continue;

                sb.Append($"Class '{kvp.Key.FullName}' inherits from '{kvp.Value.FullName}'");
            }

            if (sb.Length == 0)
                return;

            throw new InvalidOperationException($"Detected that some test classes are inheriting from each other.{Environment.NewLine}{sb}");
        }

        private IEnumerable<Assembly> GetAssemblies(Assembly assemblyToScan)
        {
            if (_assemblies.Add(assemblyToScan) == false)
                yield break;

            yield return assemblyToScan;

            foreach (var referencedAssembly in assemblyToScan.GetReferencedAssemblies().Select(Assembly.Load))
            {
                foreach (var assembly in GetAssemblies(referencedAssembly))
                    yield return assembly;
            }
        }
    }
}