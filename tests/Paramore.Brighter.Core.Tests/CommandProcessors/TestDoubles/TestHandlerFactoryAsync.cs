﻿#region Licence
/* The MIT License (MIT)
Copyright © 2014 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */

#endregion

using System;
using Paramore.Brighter.Scope;

namespace Paramore.Brighter.Core.Tests.CommandProcessors.TestDoubles
{
    class TestHandlerFactoryAsync<TRequest, TRequestHandler> : IAmAHandlerFactorySync, IAmAHandlerFactoryAsync where TRequest : class, IRequest where TRequestHandler : class, IHandleRequestsAsync<TRequest>
    {
        private readonly Func<TRequestHandler> _factoryMethod;

        public TestHandlerFactoryAsync(Func<TRequestHandler> factoryMethod)
        {
            _factoryMethod = factoryMethod;
        }

        IHandleRequestsAsync IAmAHandlerFactoryAsync.Create(Type handlerType, IAmALifetime lifetimeScope)
        {
            return _factoryMethod();
        }

        public void Release(IHandleRequestsAsync handler)
        {
            var disposable = handler as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
            handler = null;
        }

        public IHandleRequests Create(Type handlerType, IAmALifetime lifetimeScope)
        {
            return Create(handlerType, lifetimeScope);
        }

        public void Release(IHandleRequests handler)
        {
            Release(handler);
        }

        public IBrighterScope CreateScope()
        {
            return new Unscoped();
        }
    }
}
