﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Microsoft.Azure.WebJobs.Host.Listeners
{
    internal interface ISharedContextProvider
    {
        TValue GetOrCreate<TValue>(IFactory<TValue> factory);
    }
}