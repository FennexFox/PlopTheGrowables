// <copyright file="CompatibilityProbeLog.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the Apache Licence, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace PlopTheGrowables
{
    using Unity.Entities;

    /// <summary>
    /// Shared formatter for investigation logs.
    /// </summary>
    internal static class CompatibilityProbeLog
    {
        /// <summary>
        /// Log prefix used by all compatibility probes.
        /// </summary>
        internal const string Prefix = "ptgCompatibilityProbe";

        /// <summary>
        /// Formats a probe event with key-value details.
        /// </summary>
        /// <param name="eventName">Event name.</param>
        /// <param name="details">Key-value detail string.</param>
        /// <returns>Formatted log line.</returns>
        internal static string Format(string eventName, string details) => $"{Prefix} {eventName}({details})";

        /// <summary>
        /// Formats an entity identifier for logs.
        /// </summary>
        /// <param name="entity">Entity to format.</param>
        /// <returns>Entity identifier or null.</returns>
        internal static string FormatEntity(Entity entity) => entity == Entity.Null ? "null" : $"{entity.Index}:{entity.Version}";

        /// <summary>
        /// Formats a boolean for probe logging.
        /// </summary>
        /// <param name="value">Boolean value.</param>
        /// <returns>Lowercase boolean text.</returns>
        internal static string FormatBool(bool value) => value ? "true" : "false";
    }
}
