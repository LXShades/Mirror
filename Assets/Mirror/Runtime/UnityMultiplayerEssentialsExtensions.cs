using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Mirror
{
    public static class UnityMultiplayerEssentialsExtensions
    {
        /// <summary>
        /// Allows the server to use RPCs for e.g. bot characters that it "owns"
        /// </summary>
        public static bool ServerCanLocallyRunRpcs { get; set; } = false;
    }
}
