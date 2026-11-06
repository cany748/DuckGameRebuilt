using System;
using System.Collections.Generic;
using System.Reflection;

namespace AddedContent.Firebreak
{
    public static partial class Marker
    {
        [AttributeUsage(AttributeTargets.Method)]
        public class UpdateContextAttribute : MarkerAttribute
        {
            public static List<UpdateContextAttribute> All = new();

            // A delegate, not MethodInfo.Invoke: this runs every frame, and reflection stubs are
            // dynamic methods, which AOT platforms can only interpret.
            public Action Invoke { get; private set; }

            protected override void Implement()
            {
                Invoke = ((MethodInfo)Member).CreateDelegate<Action>();
                All.Add(this);
            }
        }
    }
}