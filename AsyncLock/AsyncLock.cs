﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NeoSmart
{
    public class AsyncLock
    {
        private readonly Stack<string> _reentrancy = new Stack<string>();
        //We are using this SemaphoreSlim like a posix condition variable
        //we only want to wake waiters, one or more of whom will try to obtain a different lock to do their thing
        //so long as we can guarantee no wakes are missed, the number of awakees is not important
        //If AutoResetEvent had WaitAsync we could use that instead
        internal SemaphoreSlim _retry = new SemaphoreSlim(0, 1);
        //We do not have System.Threading.Thread.* on .NET Standard without additional dependencies
        //Work around is easy: create a new ThreadLocal<T> with a random value and this is our thread id :)
        internal Guid _owningId = Guid.Empty;
        private static ThreadLocal<Guid> _threadId = new ThreadLocal<Guid>(() => Guid.NewGuid());
        public static Guid ThreadId => _threadId.Value; //public so anyone that needs a thread id can use this

        /*
         * We use two things to determine reentrancy: the caller's stack trace and the thread id
         * Here's an example where the thread id is the same but locks should block:
        */

        private class ThreadIdConflict
        {
            AsyncLock _lock = new AsyncLock();

            async void Button1_Click()
            {
                using (_lock.Lock())
                {
                    await Task.Delay(-1); //at this point, control goes back to the UI thread
                }
            }

            async void Button2_Click()
            {
                using (_lock.Lock())
                {
                    await Task.Delay(-1); //at this point, control goes back to the UI thread
                }
            }
        }

        /* When the first button is clicked, the handler is _synchronously_ executed, but the await call
         * triggers control to be returned back to the main UI thread. Then the user clicks the second 
         * button, which also _synchronously_ calls _lock.Lock(), i.e. with the same thread id as before.
         * A reentrancy check based off thread id would fail to block this second call from succeeding
         * before the using(..) block in Button1_Click() returns (several years later).
        */

        /* Unfortunately, we cannot just allow a call to bypass the lock if the stack trace is the same, either.
         * In the following example, both calls will share a stack trace, but are _not_ an example of reentrance:
        */

        /* With this AsyncLock library, this code actually works as expected:
        */
        
        private class AsyncLockTest
        {
            AsyncLock _lock = new AsyncLock();
            void Test()
            {
                //the code below will be run immediately (and asynchronously, in a new thread)
                Task.Run(async () =>
                {
                    //this first call to LockAsync() will obtain the lock without blocking
                    using (await _lock.LockAsync())
                    {
                        //this second call to LockAsync() will be recognized as being a reentrant call and go through
                        using (await _lock.LockAsync())
                        {
                            //we now hold the lock exclusively and no one else can use it for 1 minute
                            await Task.Delay(TimeSpan.FromMinutes(1));
                        }
                    }
                }).Wait(TimeSpan.FromSeconds(30));

                //this call to obtain the lock is synchronously made from the main thread
                //It will, however, block until the asynchronous code which obtained the lock above finishes
                using (_lock.Lock())
                {
                    //now we have obtained exclusive access
                }
            }
        }

        private class StackTraceConflict
        {
            AsyncLock _lock = new AsyncLock();

            async void DoSomething()
            {
                using (_lock.Lock())
                {
                    await Task.Delay(-1);
                }
            }

            void DoManySomethings()
            {
                while(true)
                {
                    DoSomething(); //no wait here!
                }
            }
        }

        /* In the above example, the _asynchronous_ calls to DoSomething() all share a stack trace.
         * It would be horribly wrong to let them bypass the lock.
        */
        
        public class InnerLock : IDisposable
        {
            private AsyncLock _parent = null;
#if DEBUG
            private bool _disposed = false;
#endif

            public InnerLock()
            {
                throw new Exception("InnerLock cannot be instantiated directly!");
            }

            internal InnerLock(AsyncLock parent)
            {
                _parent = parent;
            }

            internal async Task ObtainLockAsync()
            {
                while (!TryEnter())
                {
                    //we need to wait for someone to leave the lock before trying again
                    await _parent._retry.WaitAsync();
                }
            }

            internal void ObtainLock()
            {
                while (!TryEnter())
                {
                    //we need to wait for someone to leave the lock before trying again
                    _parent._retry.Wait();
                }
            }

            private string CleanedStackTrace
            {
                get
                {
                    //find last instance of NeoSmart.AsyncLock to get stack trace prior to that
                    var sTrace = Environment.StackTrace.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                    int skip = 0;
                    for (int i = 0; i < sTrace.Count; ++i)
                    {
                        if (sTrace[i].Contains("at NeoSmart.AsyncLock"))
                        {
                            skip = i + 1;
                        }
                    }
                    return sTrace.Skip(skip).Aggregate("", (one, all) => all = $"{one}\n{all}");
                }
            }

            private bool TryEnter()
            {
                lock (_parent._reentrancy)
                {
                    Debug.Assert((_parent._owningId == Guid.Empty) == (_parent._reentrancy.Count == 0));
                    if (_parent._owningId != Guid.Empty && _parent._owningId != AsyncLock.ThreadId)
                    {
                        //another thread currently owns the lock
                        return false;
                    }
                    if (_parent._reentrancy.Count == 0 || _parent._reentrancy.Peek().EndsWith(CleanedStackTrace))
                    {
                        //we can go in
                        _parent._owningId = AsyncLock.ThreadId;
                        _parent._reentrancy.Push(CleanedStackTrace);
                        return true;
                    }
                    else
                    {
                        //we are not allowed to go in and must wait for the lock to be come available
                        return false;
                    }
                }
            }

            public void Dispose()
            {
#if DEBUG
                Debug.Assert(!_disposed);
                _disposed = true;
#endif
                lock (_parent._reentrancy)
                {
                    _parent._reentrancy.Pop();
                    if (_parent._reentrancy.Count == 0)
                    {
                        //the owning thread is always the same so long as we are in a nested stack call
                        //we reset the owning id to null only when the lock is fully unlocked
                        _parent._owningId = Guid.Empty;
                    }
                    if (_parent._retry.CurrentCount == 0)
                    {
                        _parent._retry.Release();
                    }
                }
            }
        }

        public InnerLock Lock()
        {
            var @lock = new InnerLock(this);
            @lock.ObtainLock();
            return @lock;
        }

        public async Task<InnerLock> LockAsync()
        {
            var @lock = new InnerLock(this);
            await @lock.ObtainLockAsync();
            return @lock;
        }
    }
}