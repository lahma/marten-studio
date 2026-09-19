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

using System.Text;

namespace MartenStudio.Components.Shared;

/// <summary>
/// Formatting the studio's pages share.
/// </summary>
/// <remarks>
/// This used to read values out of whatever the API client returned by walking JSON properties and
/// reflecting over properties, because the client's trigger, calendar and job-data members were
/// untyped. They are <c>ITrigger</c>, <c>ICalendar</c> and <c>JobDataMap</c>
/// now, so the pages read them as properties and none of that is needed.
/// </remarks>
internal static class DisplayValueHelper
{
    public static string FormatKey(string? group, string? name)
    {
        string safeGroup = string.IsNullOrWhiteSpace(group) ? "DEFAULT" : group;
        string safeName = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        return safeGroup + "." + safeName;
    }

    /// <summary>
    /// The C#-style name of an assembly-qualified .NET type name, for the screens that show one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed generic's assembly-qualified name nests a whole qualified name per argument, so the one
    /// Marten records for <c>Compacted&lt;DailySales&gt;</c> is 170 characters of
    /// <c>JasperFx.Events.Compacted`1[[MartenStudio.SampleDomain.Events.DailySales,
    /// MartenStudio.SampleDomain, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]],
    /// JasperFx.Events</c> - which on the event-types screen made one column 1691px wide on its own and
    /// pushed four of the other five off the screen. What a person wants to read out of that is
    /// <c>JasperFx.Events.Compacted&lt;MartenStudio.SampleDomain.Events.DailySales&gt;</c>.
    /// </para>
    /// <para>
    /// Namespaces are kept: two event types called <c>OrderPlaced</c> in two namespaces are exactly the
    /// case this column is read to settle. The version, culture and public key are dropped at every
    /// nesting level, the arity tick is dropped, array and nested-type suffixes are kept as they are.
    /// </para>
    /// <para>
    /// It is a formatter and never a validator: anything that does not parse as an assembly-qualified
    /// name comes back exactly as it went in, because the screens render the raw value in the cell's
    /// <c>title</c> and a half-parsed name in the cell beside it would be a lie rather than a summary.
    /// </para>
    /// </remarks>
    /// <param name="assemblyQualifiedName">The name Marten recorded, or <see langword="null" />.</param>
    /// <returns>
    /// The readable name, the input unchanged when it does not parse, or <see langword="null" /> for
    /// <see langword="null" />.
    /// </returns>
    public static string? PrettyTypeName(string? assemblyQualifiedName)
    {
        if (string.IsNullOrEmpty(assemblyQualifiedName))
        {
            return assemblyQualifiedName;
        }

        int position = 0;
        StringBuilder builder = new();

        if (!TryReadType(assemblyQualifiedName, ref position, builder, nested: false)
            || position != assemblyQualifiedName.Length
            || builder.Length == 0)
        {
            return assemblyQualifiedName;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads one type specification - name, generic arguments, array suffixes and the assembly
    /// qualification that follows it - starting at <paramref name="position" />.
    /// </summary>
    /// <param name="text">The whole name being read.</param>
    /// <param name="position">Where to start, left just past what was read.</param>
    /// <param name="output">Where the readable form is written.</param>
    /// <param name="nested">
    /// Whether this is a generic argument, which ends at the <c>]</c> its caller will consume rather
    /// than at the end of the string.
    /// </param>
    /// <returns>Whether it parsed.</returns>
    private static bool TryReadType(string text, ref int position, StringBuilder output, bool nested)
    {
        StringBuilder name = new();

        while (position < text.Length)
        {
            char c = text[position];

            // The one escape the runtime's own grammar has: a type whose name really does contain a
            // bracket, a comma or a backslash carries it as `\x`.
            if (c == '\\' && position + 1 < text.Length)
            {
                name.Append(text[position + 1]);
                position += 2;
                continue;
            }

            if (c is '[' or ']' or ',')
            {
                break;
            }

            name.Append(c);
            position++;
        }

        string simpleName = StripArity(name.ToString());
        if (simpleName.Length == 0)
        {
            return false;
        }

        output.Append(simpleName);

        bool readArguments = false;
        while (position < text.Length && text[position] == '[')
        {
            if (position + 1 < text.Length && text[position + 1] == '[')
            {
                // `Type`1[[arg],[arg]]` - the generic argument list, which each type has at most one of.
                if (readArguments || !TryReadArguments(text, ref position, output))
                {
                    return false;
                }

                readArguments = true;
                continue;
            }

            // `Type[]`, `Type[,]` - an array rank, kept exactly as written.
            int close = text.IndexOf(']', position);
            if (close < 0)
            {
                return false;
            }

            output.Append(text, position, close - position + 1);
            position = close + 1;
        }

        if (position < text.Length && text[position] == ',')
        {
            // The assembly qualification: everything the reader does not want. An assembly name cannot
            // contain an unescaped `]`, so for a generic argument that bracket is where it ends.
            if (nested)
            {
                while (position < text.Length && text[position] != ']')
                {
                    position++;
                }
            }
            else
            {
                position = text.Length;
            }
        }

        return true;
    }

    /// <summary>Reads <c>[[arg],[arg]]</c> and writes <c>&lt;arg, arg&gt;</c>.</summary>
    /// <param name="text">The whole name being read.</param>
    /// <param name="position">At the opening <c>[</c> of the list; left just past its <c>]</c>.</param>
    /// <param name="output">Where the readable form is written.</param>
    /// <returns>Whether it parsed.</returns>
    private static bool TryReadArguments(string text, ref int position, StringBuilder output)
    {
        position++;

        StringBuilder arguments = new();
        while (true)
        {
            if (position >= text.Length || text[position] != '[')
            {
                return false;
            }

            position++;

            if (!TryReadType(text, ref position, arguments, nested: true))
            {
                return false;
            }

            if (position >= text.Length || text[position] != ']')
            {
                return false;
            }

            position++;

            if (position < text.Length && text[position] == ',')
            {
                position++;
                while (position < text.Length && text[position] == ' ')
                {
                    position++;
                }

                arguments.Append(", ");
                continue;
            }

            break;
        }

        if (position >= text.Length || text[position] != ']')
        {
            return false;
        }

        position++;

        output.Append('<').Append(arguments).Append('>');
        return true;
    }

    /// <summary>Drops the <c>`2</c> the runtime puts on a generic type's name, at every nesting level.</summary>
    /// <param name="name">The simple name as it was read.</param>
    /// <returns>The same name without its arity ticks.</returns>
    private static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        if (tick < 0)
        {
            return name;
        }

        StringBuilder stripped = new(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '`')
            {
                stripped.Append(name[i]);
                continue;
            }

            i++;
            while (i < name.Length && char.IsAsciiDigit(name[i]))
            {
                i++;
            }

            i--;
        }

        return stripped.ToString();
    }
}
