﻿// Copyright 2015 Destructurama Contributors, Serilog Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Destructurama.Util;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace Destructurama.Attributed
{
    class AttributedDestructuringPolicy : IDestructuringPolicy
    {
        readonly object _cacheLock = new object();
        readonly HashSet<Type> _ignored = new HashSet<Type>();
        readonly Dictionary<Type, Func<object, ILogEventPropertyValueFactory, LogEventPropertyValue>> _cache = new Dictionary<Type, Func<object, ILogEventPropertyValueFactory, LogEventPropertyValue>>();

        public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
        {
            var t = value.GetType();
            lock (_cacheLock)
            {
                if (_ignored.Contains(t))
                {
                    result = null;
                    return false;
                }

                Func<object, ILogEventPropertyValueFactory, LogEventPropertyValue> cached;
                if (_cache.TryGetValue(t, out cached))
                {
                    result = cached(value, propertyValueFactory);
                    return true;
                }
            }

            var ti = t.GetTypeInfo();

            var logAsScalar = ti.GetCustomAttribute<LogAsScalarAttribute>();
            if (logAsScalar != null)
            {
                lock (_cacheLock)
                    _cache[t] = (o, f) => logAsScalar.CreateLogEventPropertyValue(o);

            }
            else
            {
                var properties = t.GetPropertiesRecursive().ToList();
                if (properties.Any(pi =>pi.GetCustomAttribute<BaseDestructuringAttribute>() != null))
                {
                    var destructuringAttributes = properties
                        .Select(pi => new {pi, Attribute = pi.GetCustomAttribute<BaseDestructuringAttribute>()})
                        .Where(o => o.Attribute != null)
                        .ToDictionary(o => o.pi, o => o.Attribute);

                    lock (_cacheLock)
                        _cache[t] = (o, f) => MakeStructure(o, properties, destructuringAttributes, f, t);
                }
                else
                {
                    lock (_cacheLock)
                        _ignored.Add(t);
                }
            }

            return TryDestructure(value, propertyValueFactory, out result);
        }

        static LogEventPropertyValue MakeStructure(object value, IEnumerable<PropertyInfo> loggedProperties, Dictionary<PropertyInfo, BaseDestructuringAttribute> destructuringAttributes, ILogEventPropertyValueFactory propertyValueFactory, Type type)
        {
            var structureProperties = new List<LogEventProperty>();
            foreach (var pi in loggedProperties)
            {
                object propValue;
                try
                {
                    propValue = pi.GetValue(value);
                }
                catch (TargetInvocationException ex)
                {
                    SelfLog.WriteLine("The property accessor {0} threw exception {1}", pi, ex);
                    propValue = "The property accessor threw an exception: " + ex.InnerException.GetType().Name;
                }
                
                LogEventPropertyValue pv;

                if (destructuringAttributes.TryGetValue(pi, out var destructuringAttribute))
                {
                    pv = destructuringAttribute.CreateLogEventPropertyValue(propValue);
                }
                else
                {
                    pv = propertyValueFactory.CreatePropertyValue(propValue, true);
                }

                if(pv != null)
                    structureProperties.Add(new LogEventProperty(pi.Name, pv));
            }

            return new StructureValue(structureProperties, type.Name);
        }
    }
}
