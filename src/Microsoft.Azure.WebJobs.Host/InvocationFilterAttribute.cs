﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host
{
    /// <summary>
    /// Base class for declarative function invocation filters
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public abstract class InvocationFilterAttribute : Attribute, IFunctionInvocationFilter
    {
        /// <summary>
        /// Method invoked before the target function is called
        /// </summary>
        /// <param name="executingContext"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual Task OnExecutingAsync(FunctionExecutingContext executingContext, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Method invoked after the target function is called
        /// </summary>
        /// <param name="executedContext"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public virtual Task OnExecutedAsync(FunctionExecutedContext executedContext, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}