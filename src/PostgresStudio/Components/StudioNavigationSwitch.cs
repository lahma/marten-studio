#region License
/*
 * All content copyright Marko Lahma, unless otherwise indicated. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy
 * of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 *
 */
#endregion

namespace PostgresStudio.Components;

/// <summary>
/// The run-time switch that turns the studio's navigation interception off (1.2), which restores 1.1's navigation: every link, Back
/// and <c>NavigateTo</c> inside the mount is an enhanced navigation, a fetch of the whole page.
/// </summary>
/// <remarks>
/// A host sets <c>AppContext.SetSwitch("PostgresStudio.DisableNavigationInterception", true)</c> before <c>app.Run()</c>. It is
/// read every time a circuit asks and never cached, it is not an option or public API, and it is temporary (<c>docs/stability.md</c>).
/// </remarks>
internal static class StudioNavigationSwitch
{
    /// <summary>The <see cref="AppContext" /> switch's name.</summary>
    internal const string Name = "PostgresStudio.DisableNavigationInterception";

    /// <summary>Whether <c>StudioRoutes</c> asks the browser to hand it the navigation: <see langword="true" /> unless the host set the switch.</summary>
    internal static bool InterceptionEnabled => !(AppContext.TryGetSwitch(Name, out bool disabled) && disabled);
}
