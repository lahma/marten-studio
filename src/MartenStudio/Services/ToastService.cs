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

namespace MartenStudio.Services;

internal enum ToastLevel
{
    Info,
    Success,
    Error
}

internal sealed record ToastMessage(int Id, string Message, ToastLevel Level);

internal sealed class ToastService
{
    private readonly Lock gate = new();
    private readonly List<ToastMessage> messages = [];
    private int nextId;

    public event Func<Task>? Changed;

    public IReadOnlyList<ToastMessage> Messages
    {
        get
        {
            lock (gate)
            {
                return messages.ToArray();
            }
        }
    }

    public Task Info(string message)
    {
        return Add(message, ToastLevel.Info);
    }

    public Task Success(string message)
    {
        return Add(message, ToastLevel.Success);
    }

    public Task Error(string message)
    {
        return Add(message, ToastLevel.Error);
    }

    public async Task Dismiss(int id)
    {
        bool removed;
        lock (gate)
        {
            removed = messages.RemoveAll(x => x.Id == id) > 0;
        }

        if (removed)
        {
            await RaiseChangedAsync();
        }
    }

    private async Task Add(string message, ToastLevel level)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        int id = Interlocked.Increment(ref nextId);

        lock (gate)
        {
            messages.Add(new ToastMessage(id, message, level));
            while (messages.Count > 5)
            {
                messages.RemoveAt(0);
            }
        }

        await RaiseChangedAsync();
        _ = RemoveAfterDelay(id, TimeSpan.FromSeconds(4));
    }

    private async Task RaiseChangedAsync()
    {
        foreach (Delegate handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                await ((Func<Task>) handler)();
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task RemoveAfterDelay(int id, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        await Dismiss(id);
    }
}
