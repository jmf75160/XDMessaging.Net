﻿/*=============================================================================
*
*	(C) Copyright 2013, Michael Carlisle (mike.carlisle@thecodeking.co.uk)
*
*   http://www.TheCodeKing.co.uk
*  
*	All rights reserved.
*	The code and information is provided "as-is" without waranty of any kind,
*	either expressed or implied.
*
*=============================================================================
*/
using System;
using System.Collections.Generic;
using System.IO;
using TheCodeKing.Utils.Contract;
using TheCodeKing.Utils.Serialization;
using XDMessaging.Entities;
using XDMessaging.Messages;

namespace XDMessaging.Transport.IOStream
{
    /// <summary>
    /// 	A concrete implementation of IXDListener which can be used to listen for messages
    /// 	broadcast using the XDIOStreamBroadcast implementation. A Mutex is used to ensure 
    /// 	a single clean up thread removes messages after the specified timeout period. Dispose
    /// 	should be called to shut down the listener cleanly and free up resources.
    /// </summary>
    [XDListenerHint(XDTransportMode.Compatibility)]
// ReSharper disable InconsistentNaming
    public sealed class XDIoStreamListener : IXDListener
// ReSharper restore InconsistentNaming
    {
        #region Constants and Fields

        /// <summary>
        /// 	A lock object used to ensure changes to watcherList are thread-safe.
        /// </summary>
        private readonly object lockObj = new object();

        private readonly ISerializer serializer;
        private bool disposed;
        private readonly object disposeLock = new object();

        /// <summary>
        /// 	A list of FileSystemWatcher instances used for each registered channel.
        /// </summary>
        private Dictionary<string, FileSystemWatcher> watcherList;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        /// 	Default constructor.
        /// </summary>
        internal XDIoStreamListener(ISerializer serializer)
        {
            Validate.That(serializer).IsNotNull();

            this.serializer = serializer;
            watcherList = new Dictionary<string, FileSystemWatcher>(StringComparer.InvariantCultureIgnoreCase);
        }


        public bool IsAlive
        {
            get { return true; }
        }

        /// <summary>
        /// 	Deconstructor, cleans unmanaged resources only
        /// </summary>
        ~XDIoStreamListener()
        {
            Dispose(false);
        }

        #endregion

        #region Events

        /// <summary>
        /// 	The MessageReceived event used to broadcast the message to attached instances within the current appDomain.
        /// </summary>
        public event Listeners.XDMessageHandler MessageReceived;

        #endregion

        #region Implemented Interfaces

        #region IDisposable

        /// <summary>
        /// 	Dispose implementation which ensures all FileSystemWatchers
        /// 	are shut down and handlers detatched.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region IXDListener

        /// <summary>
        /// 	Sets up a new FileSystemWatcher so that messages can be received on a particular 'channel'.
        /// </summary>
        /// <param name = "channelName"></param>
        public void RegisterChannel(string channelName)
        {
            Validate.That(channelName).IsNotNullOrEmpty();

            if (!disposed)
            {
                lock (disposeLock)
                {
                    if (!disposed)
                    {
                        FileSystemWatcher watcher = EnsureWatcher(channelName);
                        watcher.EnableRaisingEvents = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 	Disables any FileSystemWatcher for a particular channel so that messages are no longer received.
        /// </summary>
        /// <param name = "channelName"></param>
        public void UnRegisterChannel(string channelName)
        {
            Validate.That(channelName).IsNotNullOrEmpty();

            if (!disposed)
            {
                lock (disposeLock)
                {
                    if (!disposed)
                    {
                        FileSystemWatcher watcher = EnsureWatcher(channelName);
                        watcher.EnableRaisingEvents = false;
                        return;
                    }
                }
            }
            throw new ObjectDisposedException("IXDListener", "This instance has been disposed.");
        }

        #endregion

        #endregion

        #region Methods

        /// <summary>
        /// 	Dispose implementation, which ensures the native window is destroyed
        /// </summary>
        private void Dispose(bool disposeManaged)
        {
            if (!disposed)
            {
                lock (disposeLock)
                {
                    if (!disposed)
                    {
                        disposed = true;
                        if (disposeManaged)
                        {
                            if (MessageReceived != null)
                            {
                                // remove all handlers
                                Delegate[] del = MessageReceived.GetInvocationList();
                                foreach (Listeners.XDMessageHandler msg in del)
                                {
                                    MessageReceived -= msg;
                                }
                            }
                            if (watcherList != null)
                            {
                                // shut down watchers
                                foreach (var watcher in watcherList.Values)
                                {
                                    watcher.EnableRaisingEvents = false;
                                    watcher.Changed -= OnMessageReceived;
                                    watcher.Dispose();
                                }
                                watcherList.Clear();
                                watcherList = null;
                            }
                        }

                    }
                }
            }
        }

        /// <summary>
        /// 	Provides a thread safe method to lookup/create a instance of FileSystemWatcher for a particular channel.
        /// </summary>
        /// <param name = "channelName"></param>
        /// <returns></returns>
        private FileSystemWatcher EnsureWatcher(string channelName)
        {
            FileSystemWatcher watcher;
            // try to get a reference to the watcher used for the current watcher
            if (!watcherList.TryGetValue(channelName, out watcher))
            {
                // if no watcher then lock the list
                lock (lockObj)
                {
                    // whilst locked double check if the item has been added since the lock was applied
                    if (!watcherList.TryGetValue(channelName, out watcher))
                    {
                        // create a new watcher for the given channel, by default this is not enabled.
                        string folder = XDIOStreamBroadcaster.GetChannelDirectory(channelName);
                        watcher = new FileSystemWatcher(folder, "*.msg")
                                      {
                                          NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite
                                      };
                        watcher.Changed += OnMessageReceived;
                        watcherList.Add(channelName, watcher);
                    }
                }
            }
            return watcher;
        }

        private void OnMessageReceived(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed)
            {
                Action<string> action = ProcessMessage;
                // process message async
                action.BeginInvoke(e.FullPath, action.EndInvoke, null);
            }
        }

        /// <summary>
        /// 	The FileSystemWatcher event that is triggered when a new file is created in the channel temporary
        /// 	directory. This dispatches the MessageReceived event.
        /// </summary>
        private void ProcessMessage(string fullPath)
        {
            try
            {
                // check if file exists
                if (File.Exists(fullPath))
                {
                    string rawmessage;
                    // try to load the file in shared access mode
                    using (var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            rawmessage = reader.ReadToEnd();
                        }
                    }

                    var dataGram = serializer.Deserialize<DataGram>(rawmessage);
                    if (dataGram.IsValid)
                    {
                        // dispatch the message received event
                        MessageReceived(this, new XDMessageEventArgs(dataGram));
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // if for any reason the file was deleted before the message could be read from the file,
                // then can safely ignore this message
            }
            catch (UnauthorizedAccessException ue)
            {
                throw new UnauthorizedAccessException(
                    string.Format("Unable to bind to channel as access is denied." +
                                  " Ensure the process has read/write access to the directory '{0}'.", fullPath),
                    ue);
            }
            catch (IOException ie)
            {
                throw new IOException(string.Format("There was an unexpected IO error binding to a channel." +
                                                    " Ensure the process is unable to read/write to directory '{0}'.",
                                                    fullPath), ie);
            }
        }

        #endregion
    }
}