using DuckGame;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace AddedContent.Firebreak
{
    public static partial class Marker
    {
        /// <summary>
        /// like a drawing context... but complex...
        /// </summary>
        [AttributeUsage(AttributeTargets.Method, Inherited = false)]
        public sealed class ComplexDrawingContextAttribute : MarkerAttribute
        {
            public static List<ComplexDrawingContextAttribute> AllDrawingContexts = new();

            private Action<Layer> _invoke;

            protected override void Implement()
            {
                MethodInfo method = (MethodInfo)Member;
                ParameterInfo[] parameters = method.GetParameters();

                if (parameters.Length != 1 || parameters[0].ParameterType != typeof(Layer))
                    throw new TargetParameterCountException("ComplexDrawingContext method structure should be FUNC(DuckGame.Layer)");
                
                _invoke = method.CreateDelegate<Action<Layer>>();
                AllDrawingContexts.Add(this);
            }

            public static void ExecuteAll(Layer layer)
            {
                foreach (ComplexDrawingContextAttribute marker in AllDrawingContexts)
                {
                    marker._invoke(layer);
                }
            }
        }
    }
}