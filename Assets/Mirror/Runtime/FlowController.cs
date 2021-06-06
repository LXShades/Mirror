using System.Collections.Generic;
using UnityEngine;

namespace Mirror
{
    /// <summary>
    /// A utility that buffers messages (PushMessage) and releases them (TryPopMessage) in a variably delayed, smooth fashion desigend to emulate how they were sent
    ///
    /// FlowControlSettings can be customised to achieve different smoothing results from smoother, delayed messages to rougher, immediate messages
    /// </summary>
    public class FlowController<TMessage>
    {
        /// <summary>
        /// Received messages paired with the time they were sent (not received!)
        /// </summary>
        private HistoryList<TMessage> receivedMessages = new HistoryList<TMessage>();

        private float localToRemoteTime = 0f;

        public static float localTime => Time.unscaledTime;

        /// <summary>
        /// Number of messages that have been buffered and pending eventual (but not necessarily immediate) release
        /// Do not assume TryPopMessage to return anything if this is nonzero, as this figure includes messages still being delayed.
        /// </summary>
        public int numBufferedPendingMessages => receivedMessages.ClosestIndexAfter(lastPoppedMessageSentTime, 0f) + 1;

        public float currentDelay { get; private set; }
        public float lastPoppedMessageSentTime { get; private set; } = -1f;

        public FlowControlSettings flowControlSettings = FlowControlSettings.Default;

        private const int MaxTimeGapHistory = 512;

        private List<float> timeGaps = new List<float>(MaxTimeGapHistory);

        private const float SecondsPerFlowRecalculation = 5f; // only if the buffer isn't filled, or we're just starting
        private float timeOfLastFlowRecalculation = -SecondsPerFlowRecalculation;

        /// <summary>
        /// Call when first receiving a message. sentTime, if available, should refer to the time that the remote party sent the message, in any format
        /// </summary>
        public void PushMessage(TMessage message, float sentTime)
        {
            receivedMessages.Insert(sentTime, message);
            timeGaps.Add(localTime - sentTime);

            RecalculateFlow();

            // cleanup ancient messages if setup to do so
            if (flowControlSettings.maxMessageAge > -1)
                receivedMessages.Prune(Mathf.Max(localTime - localToRemoteTime - flowControlSettings.maxMessageAge));
        }

        /// <summary>
        /// Tries to retrieve the next flow-controlled message, if available.
        /// skipOutdatedMessages determines whether to discard messages that aren't the latest
        /// </summary>
        public bool TryPopMessage(out TMessage message, bool skipOutdatedMessages)
        {
            float targetSentTime = localTime - localToRemoteTime;
            int latestIndex = receivedMessages.ClosestIndexBefore(targetSentTime, flowControlSettings.earlyReleaseTolerance);

            if (latestIndex != -1)
            {
                if (skipOutdatedMessages)
                {
                    // skip the older ones, release the newest old one
                    message = receivedMessages[latestIndex];
                    lastPoppedMessageSentTime = receivedMessages.TimeAt(latestIndex);

                    receivedMessages.Prune(targetSentTime);

                    return true;
                }
                else
                {
                    // release the oldest one
                    message = receivedMessages[receivedMessages.Count - 1];
                    lastPoppedMessageSentTime = receivedMessages.TimeAt(receivedMessages.Count - 1);

                    receivedMessages.RemoveAt(receivedMessages.Count - 1);

                    return true;
                }
            }

            message = default;
            return false;
        }

        /// <summary>
        /// Resets the flow controller meaning time can start again from 0
        /// </summary>
        public void Reset()
        {
            receivedMessages.Clear();
            lastPoppedMessageSentTime = -1f;
        }

        private void RecalculateFlow()
        {
            if (timeGaps.Count >= MaxTimeGapHistory || localTime - timeOfLastFlowRecalculation >= SecondsPerFlowRecalculation)
            {
                if (timeGaps.Count > 0)
                {
                    timeGaps.Sort();

                    float minGap = timeGaps[0];
                    int topPercentileIndex = (int)Mathf.Min((1f - flowControlSettings.upperPercentile / 100f) * timeGaps.Count);
                    currentDelay = Mathf.Clamp(
                        Mathf.Min(timeGaps[topPercentileIndex], timeGaps.Count - 1) + flowControlSettings.addToDelay - minGap,
                        flowControlSettings.minDelay,
                        flowControlSettings.maxDelay);
                    localToRemoteTime = minGap + currentDelay;

                    timeGaps.Clear();
                    timeOfLastFlowRecalculation = localTime;
                }
                else
                {
                    Debug.Assert(receivedMessages.Count == 0);
                    localToRemoteTime = 0f; // we don't know yet
                }
            }
        }

        public override string ToString()
        {
            return
                $"FlowController<{typeof(TMessage).Name}>:Msgs/Delay/Remot/Lcl/Diff {numBufferedPendingMessages}/{(int)(currentDelay * 1000)}ms/{lastPoppedMessageSentTime.ToString("F1")}/{localTime.ToString("F1")}/{localToRemoteTime.ToString("F1")}";
        }
    }

    [System.Serializable]
    public struct FlowControlSettings
    {
        public static FlowControlSettings Default = new FlowControlSettings()
        {
            jitterSampleSize = 3f,
            upperPercentile = 5f,
            maxDelay = 0.1f,
            minDelay = 0f,
            maxMessageAge = -1f,
            earlyReleaseTolerance = 0.01f // about a half-frame at 50fps
        };

        public float jitterSampleSize;

        public float upperPercentile; // as a percentage. 1 means highest 1% of jitter controls the overall delay of the flow
        public float addToDelay;

        public float maxDelay;
        public float minDelay;

        public float maxMessageAge;

        public float earlyReleaseTolerance;
    }
}
