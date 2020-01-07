﻿using System;
using Ruffles.Channeling;
using Ruffles.Configuration;
using Ruffles.Connections;
using Ruffles.Core;
using Ruffles.Memory;
using Ruffles.Time;
using Ruffles.Utils;

namespace Ruffles.Messaging
{
    internal static class ChannelRouter
    {
        internal static void HandleIncomingAck(ArraySegment<byte> payload, Connection connection, SocketConfig config, MemoryManager memoryManager)
        {
            // This is where all data packets arrive after passing the connection handling.

            byte channelId = payload.Array[payload.Offset];

            if (channelId < 0 || channelId >= connection.Channels.Length)
            {
                // ChannelId out of range
                if (Logging.CurrentLogLevel <= LogLevel.Warning) Logging.LogWarning("Got ack on channel out of range. [ChannelId=" + channelId + "]");
                return;
            }

            IChannel channel = connection.Channels[channelId];

            if (channel != null)
            {
                channel.HandleAck(new ArraySegment<byte>(payload.Array, payload.Offset + 1, payload.Count - 1));
            }
            else
            {
                if (Logging.CurrentLogLevel <= LogLevel.Warning) Logging.LogWarning("Receive ack failed because the channel is not assigned");
            }
        }

        internal static void HandleIncomingMessage(ArraySegment<byte> payload, Connection connection, SocketConfig config, MemoryManager memoryManager)
        {
            // This is where all data packets arrive after passing the connection handling.

            byte channelId = payload.Array[payload.Offset];

            if (channelId < 0 || channelId >= connection.Channels.Length)
            {
                // ChannelId out of range
                if (Logging.CurrentLogLevel <= LogLevel.Warning) Logging.LogWarning("Got message on channel out of range. [ChannelId=" + channelId + "]");
                return;
            }

            IChannel channel = connection.Channels[channelId];

            if (channel != null)
            {
                HeapPointers incomingPointers = channel.HandleIncomingMessagePoll(new ArraySegment<byte>(payload.Array, payload.Offset + 1, payload.Count - 1));

                if (incomingPointers != null)
                {
                    // There is new packets
                    for (int i = 0; i < incomingPointers.VirtualCount; i++)
                    {
                        MemoryWrapper wrapper = (MemoryWrapper)incomingPointers.Pointers[i];

                        HeapMemory memory = null;

                        if (wrapper.AllocatedMemory != null)
                        {
                            memory = wrapper.AllocatedMemory;
                        }

                        if (wrapper.DirectMemory != null)
                        {
                            // Alloc memory
                            memory = memoryManager.AllocHeapMemory((uint)wrapper.DirectMemory.Value.Count);

                            // Copy payload to borrowed memory
                            Buffer.BlockCopy(wrapper.DirectMemory.Value.Array, wrapper.DirectMemory.Value.Offset, memory.Buffer, 0, wrapper.DirectMemory.Value.Count);
                        }

                        if (memory != null)
                        {
                            // Send to userspace
                            connection.Socket.PublishEvent(new NetworkEvent()
                            {
                                Connection = connection,
                                Socket = connection.Socket,
                                Type = NetworkEventType.Data,
                                AllowUserRecycle = true,
                                Data = new ArraySegment<byte>(memory.Buffer, (int)memory.VirtualOffset, (int)memory.VirtualCount),
                                InternalMemory = memory,
                                SocketReceiveTime = NetTime.Now,
                                ChannelId = channelId,
                                MemoryManager = memoryManager
                            });
                        }

                        // Dealloc the wrapper
                        memoryManager.DeAlloc(wrapper);
                    }

                    // Dealloc the pointers
                    memoryManager.DeAlloc(incomingPointers);
                }
            }
            else
            {
                if (Logging.CurrentLogLevel <= LogLevel.Warning) Logging.LogWarning("Receive message failed because the channel is not assigned");
            }
        }

        internal static void SendMessage(ArraySegment<byte> payload, Connection connection, byte channelId, bool noMerge, MemoryManager memoryManager)
        {
            if (channelId < 0 || channelId >= connection.Channels.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(channelId), channelId, "ChannelId was out of range");
            }

            IChannel channel = connection.Channels[channelId];

            if (channel != null)
            {
                HeapPointers memoryPointers = channel.CreateOutgoingMessage(payload, out bool dealloc);

                if (memoryPointers != null)
                {
                    for (int i = 0; i < memoryPointers.VirtualCount; i++)
                    {
                        connection.Send(new ArraySegment<byte>(((HeapMemory)memoryPointers.Pointers[i]).Buffer, (int)((HeapMemory)memoryPointers.Pointers[i]).VirtualOffset, (int)((HeapMemory)memoryPointers.Pointers[i]).VirtualCount), noMerge);
                    }

                    if (dealloc)
                    {
                        // DeAlloc the memory again. This is done for unreliable channels that dont need the message after the initial send.
                        for (int i = 0; i < memoryPointers.VirtualCount; i++)
                        {
                            memoryManager.DeAlloc(((HeapMemory)memoryPointers.Pointers[i]));
                        }
                    }

                    // Dealloc the array always.
                    memoryManager.DeAlloc(memoryPointers);
                }
            }
            else
            {
                if (Logging.CurrentLogLevel <= LogLevel.Warning) Logging.LogWarning("Sending packet failed because the channel is not assigned");
            }
        }
    }
}