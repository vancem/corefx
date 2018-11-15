﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
#if ALLOW_PARTIALLY_TRUSTED_CALLERS
    using System.Security;
#endif
using System.Threading;

namespace System.Diagnostics
{
    /// <summary>
    /// Activity represents operation with context to be used for logging.
    /// Activity has operation name, Id, start time and duration, tags and baggage.
    ///  
    /// Current activity can be accessed with static AsyncLocal variable Activity.Current.
    /// 
    /// Activities should be created with constructor, configured as necessary
    /// and then started with Activity.Start method which maintains parent-child
    /// relationships for the activities and sets Activity.Current.
    /// 
    /// When activity is finished, it should be stopped with static Activity.Stop method.
    /// 
    /// No methods on Activity allow exceptions to escape as a response to bad inputs.
    /// They are thrown and caught (that allows Debuggers and Monitors to see the error)
    /// but the exception is suppressed, and the operation does something reasonable (typically
    /// doing nothing).  
    /// </summary>
    public partial class Activity
    {
        /// <summary>
        /// An operation name is a COARSEST name that is useful grouping/filtering. 
        /// The name is typically a compile-time constant.   Names of Rest APIs are
        /// reasonable, but arguments (e.g. specific accounts etc), should not be in
        /// the name but rather in the tags.  
        /// </summary>
        public string OperationName { get; }

        /// <summary>
        /// This is an ID that is specific to a particular request.   Filtering
        /// to a particular ID insures that you get only one request that matches.  
        /// Id has a hierarchical structure: '|root-id.id1_id2.id3_' Id is generated when 
        /// <see cref="Start"/> is called by appending suffix to Parent.Id
        /// or ParentId; Activity has no Id until it started
        /// <para/>
        /// See <see href="https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/ActivityUserGuide.md#id-format"/> for more details
        /// </summary>
        /// <example>
        /// Id looks like '|a000b421-5d183ab6.1.8e2d4c28_1.':<para />
        ///  - '|a000b421-5d183ab6.' - Id of the first, top-most, Activity created<para />
        ///  - '|a000b421-5d183ab6.1.' - Id of a child activity. It was started in the same process as the first activity and ends with '.'<para />
        ///  - '|a000b421-5d183ab6.1.8e2d4c28_' - Id of the grand child activity. It was started in another process and ends with '_'<para />
        /// 'a000b421-5d183ab6' is a <see cref="RootId"/> for the first Activity and all its children
        /// </example>
        public string Id { get; private set; }

        /// <summary>
        /// The time that operation started.  It will typically be initialized when <see cref="Start"/>
        /// is called, but you can set at any time via <see cref="SetStartTime(DateTime)"/>.
        /// </summary>
        public DateTime StartTimeUtc { get; private set; }

        /// <summary>
        /// If the Activity that created this activity is from the same process you can get 
        /// that Activity with Parent.  However, this can be null if the Activity has no
        /// parent (a root activity) or if the Parent is from outside the process.
        /// </summary>
        /// <seealso cref="ParentId"/>
        public Activity Parent { get; private set; }

        /// <summary>
        /// If the parent for this activity comes from outside the process, the activity
        /// does not have a Parent Activity but MAY have a ParentId (which was deserialized from
        /// from the parent).   This accessor fetches the parent ID if it exists at all.  
        /// Note this can be null if this is a root Activity (it has no parent)
        /// <para/>
        /// See <see href="https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/ActivityUserGuide.md#id-format"/> for more details
        /// </summary>
        public string ParentId { get; private set; }

        /// <summary>
        /// Root Id is substring from Activity.Id (or ParentId) between '|' (or beginning) and first '.'.
        /// Filtering by root Id allows to find all Activities involved in operation processing.
        /// RootId may be null if Activity has neither ParentId nor Id.
        /// See <see href="https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/ActivityUserGuide.md#id-format"/> for more details
        /// </summary>
        public string RootId
        {
            get
            {
                //we expect RootId to be requested at any time after activity is created, 
                //possibly even before it was started for sampling or logging purposes
                //Presumably, it will be called by logging systems for every log record, so we cache it.
                if (_rootId == null)
                {
                    if (Id != null)
                    {
                        _rootId = GetRootId(Id);
                    }
                    else if (ParentId != null)
                    {
                        _rootId = GetRootId(ParentId);
                    }
                }
                return _rootId;
            }
        }

        /// <summary>
        /// Tags are string-string key-value pairs that represent information that will
        /// be logged along with the Activity to the logging system.   This information
        /// however is NOT passed on to the children of this activity.
        /// </summary>
        /// <seealso cref="Baggage"/>
        public IEnumerable<KeyValuePair<string, string>> Tags
        {
            get
            {
                for (var tags = _tags; tags != null; tags = tags.Next)
                    yield return tags.keyValue;
            }
        }

        /// <summary>
        /// Baggage is string-string key-value pairs that represent information that will
        /// be passed along to children of this activity.   Baggage is serialized 
        /// when requests leave the process (along with the ID).   Typically Baggage is
        /// used to do fine-grained control over logging of the activity and any children.  
        /// In general, if you are not using the data at runtime, you should be using Tags 
        /// instead. 
        /// </summary> 
        public IEnumerable<KeyValuePair<string, string>> Baggage
        {
            get
            {
                for (var activity = this; activity != null; activity = activity.Parent)
                    for (var baggage = activity._baggage; baggage != null; baggage = baggage.Next)
                        yield return baggage.keyValue;
            }
        }

        /// <summary>
        /// Returns the value of the key-value pair added to the activity with <see cref="AddBaggage(string, string)"/>.
        /// Returns null if that key does not exist.  
        /// </summary>
        public string GetBaggageItem(string key)
        {
            foreach (var keyValue in Baggage)
                if (key == keyValue.Key)
                    return keyValue.Value;
            return null;
        }

        /* Constructors  Builder methods */

        /// <summary>
        /// Note that Activity has a 'builder' pattern, where you call the constructor, a number of 'Set*' and 'Add*' APIs and then
        /// call <see cref="Start"/> to build the activity.  You MUST call <see cref="Start"/> before using it.
        /// </summary>
        /// <param name="operationName">Operation's name <see cref="OperationName"/></param>
        public Activity(string operationName)
        {
            if (string.IsNullOrEmpty(operationName))
            {
                NotifyError(new ArgumentException($"{nameof(operationName)} must not be null or empty"));
                return;
            }

            OperationName = operationName;
        }

        /// <summary>
        /// Update the Activity to have a tag with an additional 'key' and value 'value'.
        /// This shows up in the <see cref="Tags"/>  enumeration.   It is meant for information that
        /// is useful to log but not needed for runtime control (for the latter, <see cref="Baggage"/>)
        /// </summary>
        /// <returns>'this' for convenient chaining</returns>
        public Activity AddTag(string key, string value)
        {
            _tags = new KeyValueListNode() { keyValue = new KeyValuePair<string, string>(key, value), Next = _tags };
            return this;
        }

        /// <summary>
        /// Update the Activity to have baggage with an additional 'key' and value 'value'.
        /// This shows up in the <see cref="Baggage"/> enumeration as well as the <see cref="GetBaggageItem(string)"/>
        /// method.
        /// Baggage is meant for information that is needed for runtime control.   For information 
        /// that is simply useful to show up in the log with the activity use <see cref="Tags"/>.
        /// Returns 'this' for convenient chaining.
        /// </summary>
        /// <returns>'this' for convenient chaining</returns>
        public Activity AddBaggage(string key, string value)
        {
            _baggage = new KeyValueListNode() { keyValue = new KeyValuePair<string, string>(key, value), Next = _baggage };
            return this;
        }

        /// <summary>
        /// Updates the Activity To indicate that the activity with ID <paramref name="parentId"/>
        /// caused this activity.   This is intended to be used only at 'boundary' 
        /// scenarios where an activity from another process logically started 
        /// this activity. The Parent ID shows up the Tags (as well as the ParentID 
        /// property), and can be used to reconstruct the causal tree.  
        /// Returns 'this' for convenient chaining.
        /// </summary>
        /// <param name="parentId">The id of the parent operation.</param>
        public Activity SetParentId(string parentId)
        {
            if (Parent != null)
            {
                NotifyError(new InvalidOperationException($"Trying to set {nameof(ParentId)} on activity which has {nameof(Parent)}"));
            }
            else if (ParentId != null)
            {
                NotifyError(new InvalidOperationException($"{nameof(ParentId)} is already set"));
            }
            else if (string.IsNullOrEmpty(parentId))
            {
                NotifyError(new ArgumentException($"{nameof(parentId)} must not be null or empty"));
            }
            else
            {
                ParentId = parentId;
                IdFormat = Parent.IdFormat;
            }
            return this;
        }

        /// <summary>
        /// Update the Activity to set start time
        /// </summary>
        /// <param name="startTimeUtc">Activity start time in UTC (Greenwich Mean Time)</param>
        /// <returns>'this' for convenient chaining</returns>
        public Activity SetStartTime(DateTime startTimeUtc)
        {
            if (startTimeUtc.Kind != DateTimeKind.Utc)
            {
                NotifyError(new InvalidOperationException($"{nameof(startTimeUtc)} is not UTC"));
            }
            else
            {
                StartTimeUtc = startTimeUtc;
            }
            return this;
        }

        /// <summary>
        /// Update the Activity to set <see cref="Duration"/>
        /// as a difference between <see cref="StartTimeUtc"/>
        /// and <paramref name="endTimeUtc"/>.
        /// </summary>
        /// <param name="endTimeUtc">Activity stop time in UTC (Greenwich Mean Time)</param>
        /// <returns>'this' for convenient chaining</returns>
        public Activity SetEndTime(DateTime endTimeUtc)
        {
            if (endTimeUtc.Kind != DateTimeKind.Utc)
            {
                NotifyError(new InvalidOperationException($"{nameof(endTimeUtc)} is not UTC"));
            }
            else
            {
                Duration = endTimeUtc - StartTimeUtc;
                if (Duration.Ticks <= 0)
                    Duration = new TimeSpan(1); // We want Duration of 0 to mean  'EndTime not set)
            }
            return this;
        }

        /// <summary>
        /// If the Activity has ended (<see cref="Stop"/> or <see cref="SetEndTime"/> was called) then this is the delta
        /// between <see cref="StartTimeUtc"/> and end.   If Activity is not ended and <see cref="SetEndTime"/> was not called then this is 
        /// <see cref="TimeSpan.Zero"/>.
        /// </summary>
        public TimeSpan Duration { get; private set; }

        /// <summary>
        /// Starts activity
        /// <list type="bullet">
        /// <item>Sets <see cref="Parent"/> to hold <see cref="Current"/>.</item>
        /// <item>Sets <see cref="Current"/> to this activity.</item>
        /// <item>If <see cref="StartTimeUtc"/> was not set previously, sets it to <see cref="DateTime.UtcNow"/>.</item>
        /// <item>Generates a unique <see cref="Id"/> for this activity.</item>
        /// </list>
        /// Use <see cref="DiagnosticSource.StartActivity(Activity, object)"/> to start activity and write start event.
        /// </summary>
        /// <seealso cref="DiagnosticSource.StartActivity(Activity, object)"/>
        /// <seealso cref="SetStartTime(DateTime)"/>
        public Activity Start()
        {
            if (Id != null)
            {
                NotifyError(new InvalidOperationException("Trying to start an Activity that was already started"));
            }
            else
            {
                if (ParentId == null)
                {
                    var parent = Current;
                    if (parent != null)
                    {
                        ParentId = parent.Id;

                        // The parent change should not form a loop.   We are actually guaranteed this because
                        // 1. Unstarted activities can't be 'Current' (thus can't be 'parent'), we throw if you try.  
                        // 2. All started activities have a finite parent change (by inductive reasoning).  
                        Parent = parent;
                    }
                }

                if (StartTimeUtc == default(DateTime))
                {
                    StartTimeUtc = GetUtcNow();
                }

                if (IdFormat == ActivityIdFormat.W3C || (ParentId == null && DefaultIdFormat == ActivityIdFormat.W3C))
                {
                    Id = GenerateW3CId();
                    IdFormat = ActivityIdFormat.W3C;
                }
                else
                {
                    Id = GenerateId();
                    IdFormat = ActivityIdFormat.Hierarchical;
                }
                SetCurrent(this);
            }
            return this;
        }

        /// <summary>
        /// Stops activity: sets <see cref="Current"/> to <see cref="Parent"/>.
        /// If end time was not set previously, sets <see cref="Duration"/> as a difference between <see cref="DateTime.UtcNow"/> and <see cref="StartTimeUtc"/>
        /// Use <see cref="DiagnosticSource.StopActivity(Activity, object)"/>  to stop activity and write stop event.
        /// </summary>
        /// <seealso cref="DiagnosticSource.StopActivity(Activity, object)"/>
        /// <seealso cref="SetEndTime(DateTime)"/>
        public void Stop()
        {
            if (Id == null)
            {
                NotifyError(new InvalidOperationException("Trying to stop an Activity that was not started"));
                return;
            }

            if (!isFinished)
            {
                isFinished = true;

                if (Duration == TimeSpan.Zero)
                {
                    SetEndTime(GetUtcNow());
                }

                SetCurrent(Parent);
            }
        }

        /* W3C support functionality (see https://w3c.github.io/trace-context) */

        /// <summary>
        /// Holds the W3C 'tracestate' header.   
        /// 
        /// Tracestate is intended to carry information supplemental to trace identity contained 
        /// in traceparent. List of key value pairs carried by tracestate convey information 
        /// about request position in multiple distributed tracing graphs. It is typically used 
        /// by distributed tracing systems and should not be used as a general purpose baggage
        /// as this use may break correlation of a distributed trace.
        /// 
        /// Logically it is just a kind of baggage (if flows just like baggage), but because
        /// it is expected to be special cased (it has its own HTTP header), it is more 
        /// convenient/efficient if it is not lumped in with other baggage.   
        /// </summary>
        public string TraceState
        {
            get
            {
                for (var activity = this; activity != null; activity = activity.Parent)
                {
                    var val = activity._traceState;
                    if (val != null)
                        return val;
                }
                return null;
            }
            set
            {
                _traceState = value;
            }
        }

        /// <summary>
        /// Returns the format for the ID.   
        /// </summary>
        public ActivityIdFormat IdFormat { get; private set; }

        /* static state (configuration) */
        /// <summary>
        /// Activity tries to use the same format for IDs as its parent.
        /// However if the activity has no parent, it has to do something.   
        /// This determines the default format we use.  
        /// </summary>
        public static ActivityIdFormat DefaultIdFormat
        {
            get { return s_DefaultIdFormat; }
            set
            {
                if (!(ActivityIdFormat.Hierarchical <= value && value <= ActivityIdFormat.W3C))
                    throw new ArgumentException($"value must be a valid ActivityIDFormat value");
                s_DefaultIdFormat = value;
            }
        }

        /* Activity Sampling Support */
        /// <summary>
        /// If true (set by StartWithSampling) 
        /// </summary>
        public bool Recording { get; private set; }

        /// <summary>
        /// CreateActivityIfRecording is intended to be used to support activity sampling.  It is to 
        /// be used when the activity has no parent (it might have an external Parent ID), and the 
        /// activity is not too big (otherwise when logging is on, too much recording happens.  
        /// 
        /// You pass the operation name, and this method will return an Activity (if this activity 
        /// is to be recorded), or null if it is not.   It is expected that you set Activity.Current with this.  
        /// 
        /// This overload is meant to be used when the activity has no logical parent (e.g. it did not
        /// come from outside the process, it is a new top-level piece of work).  
        /// </summary>
        static Activity CreateActivityIfRecording(string operationName)
        {
            return CreateActivityIfRecording(operationName, false, Sampling.NextHash());
        }

        /// <summary>
        /// CreateActivityIfRecording is intended to be used to support activity sampling.  It is to 
        /// be used when the activity has no parent (it might have an external Parent ID), and the 
        /// activity is not too big (otherwise when logging is on, too much recording happens.  
        /// 
        /// You pass the operation name, and this method will return an Activity (if this activity 
        /// is to be recorded), or null if it is not.   It is expected that you set Activity.Current with this.  
        /// 
        /// This overload is intended to be used when the activity comes from outside the process and
        /// you have a activity ID for your parent.  If 'parentID' is in W3C format, we try to respect
        /// the 'recording' bit of the ID, otherwise we use the last 2 bytes (4 chars) as a hash 
        /// to determine if it will be selected as a 'wildcard' sample.   
        /// </summary>
        static Activity CreateActivityIfRecording(string operationName, string parentID)
        {
            if (IsW3CId(parentID))
            {
                bool recordingBit = ((parentID[54] - '0') & 1) != 0;   // Convert the last character to an byte and take the LSB which is the Recording bit.  

                // Convert the LCB of the SpanID to a 64bit  binary number.  
                int hash = ((((parentID[48] - '0') & 0xF) << 4 + ((parentID[49] - '0') & 0xF)) << 4 + ((parentID[50] - '0') & 0xF)) << ((parentID[51] - '0') & 0xF);
                return CreateActivityIfRecording(operationName, recordingBit, (ushort)hash);
            }
            return CreateActivityIfRecording(operationName, false, (ushort)parentID.GetHashCode());
        }

        /// <summary>
        ///  TODO make public?
        /// </summary>
        private static Activity CreateActivityIfRecording(string operationName, bool recordingDesired, ushort parentIDHash)
        {
            if (!Sampling.ShouldSample(recordingDesired, parentIDHash))
                return null;
            Activity ret = new Activity(operationName);
            ret.Recording = true;
            return ret;
        }

        /// <summary>
        /// Returns true if ActivitySampling is currently on.   You turn on ActivitySampling by 
        /// using RegisterActivityConfig.  
        /// </summary>
        static bool IsActivitySamplingEnabled { get { return s_sampling.IsActivitySampledEnabled; } }

        /// <summary>
        /// Activities support sampling.   Logging systems are strongly encouraged to support sampling 
        /// by adding the following check before they actually log (the logging system gets to decide 
        /// what verbosity level does this (we recommend info or more verbose).
        /// 
        /// To turn on logging you create a ActivityConfig class (which specifies the degree of filtering)
        /// and call RegisterActivityConfig.  Once registered this config is active until the config object
        /// is Disposed (Or dies and is finalized).    This way multiple logging systems can ask for 
        /// varying levels of sampling and you get the union (Most verbose) of what is currently active. 
        /// 
        /// Finally it should be noted that all logging systems have to be prepared to receive more events
        /// than they asked for (thus sampling is a perf optimization).   If the logging system wishes 
        /// to insure some limit, it should do that filtering after receiving the event (when it has complete 
        /// control).   The goal of activity sampling is to simply make logging CHEAP.   
        /// </summary>
        static void RegisterActivityConfig(ActivityConfig config) { return s_sampling.RegisterSamplingConfig(config); }

        #region private 
        /// <summary>
        /// Returns true if 'id' has the format of a WC3 id see https://w3c.github.io/trace-context
        /// </summary>
        private static bool IsW3CId(string id)
        {
            // A W3CId is  
            //  * 2 chars Version
            //  * 32 chars traceId
            //  * 16 chars spanId
            //  * 2 chars flags 
            //  * 3 chars - separators.  
            //  = 55 chars (see https://w3c.github.io/trace-context)
            // We require that all non-WC3IDs NOT start with a digit.  
            return id.Length == 55 && char.IsDigit(id[0]);
        }
        private static bool IsW3CId(Span<byte> id)
        {
            return id.Length == 55 && char.IsDigit((char) id[0]);
        }

        private string GenerateW3CId()
        {
            string newSpanId = Guid.NewGuid().ToString("n").Substring(16, 16);
            if (ParentId != null)
            {
                return ParentId.Substring(0, 36) + newSpanId + ParentId.Substring(52, 3);
            }
            else
            {
                string newTraceId = Guid.NewGuid().ToString("n");
                return "00-" + newTraceId + "-" + newSpanId + "-00";
            }
        }

        private static void NotifyError(Exception exception)
        {
            // Throw and catch the exception.  This lets it be seen by the debugger
            // ETW, and other monitoring tools.   However we immediately swallow the
            // exception.   We may wish in the future to allow users to hook this 
            // in other useful ways but for now we simply swallow the exceptions.  
            try
            {
                throw exception;
            }
            catch { }
        }

        private string GenerateId()
        {
            string ret;
            if (Parent != null)
            {
                // Normal start within the process
                Debug.Assert(!string.IsNullOrEmpty(Parent.Id));
                ret = AppendSuffix(Parent.Id, Interlocked.Increment(ref Parent._currentChildId).ToString(), '.');
            }
            else if (ParentId != null)
            {
                // Start from outside the process (e.g. incoming HTTP)
                Debug.Assert(ParentId.Length != 0);

                //sanitize external RequestId as it may not be hierarchical. 
                //we cannot update ParentId, we must let it be logged exactly as it was passed.
                string parentId = ParentId[0] == '|' ? ParentId : '|' + ParentId;

                char lastChar = parentId[parentId.Length - 1];
                if (lastChar != '.' && lastChar != '_')
                {
                    parentId += '.';
                }

                ret = AppendSuffix(parentId, Interlocked.Increment(ref s_currentRootId).ToString("x"), '_');
            }
            else
            {
                // A Root Activity (no parent).  
                ret = GenerateRootId();
            }
            // Useful place to place a conditional breakpoint.  
            return ret;
        }

        private string GetRootId(string id)
        {
            // If this is a W3C ID it has the format Version2-TraceId32-SpanId16-Flags2
            // and the root ID is the TraceId.   
            if (IdFormat == ActivityIdFormat.W3C)
                return id.Substring(3, 32);

            //id MAY start with '|' and contain '.'. We return substring between them
            //ParentId MAY NOT have hierarchical structure and we don't know if initially rootId was started with '|',
            //so we must NOT include first '|' to allow mixed hierarchical and non-hierarchical request id scenarios
            int rootEnd = id.IndexOf('.');
            if (rootEnd < 0)
                rootEnd = id.Length;
            int rootStart = id[0] == '|' ? 1 : 0;
            return id.Substring(rootStart, rootEnd - rootStart);
        }

        private string AppendSuffix(string parentId, string suffix, char delimiter)
        {
#if DEBUG
            suffix = OperationName.Replace('.', '-') + "-" + suffix;
#endif
            if (parentId.Length + suffix.Length < RequestIdMaxLength)
                return parentId + suffix + delimiter;

            //Id overflow:
            //find position in RequestId to trim
            int trimPosition = RequestIdMaxLength - 9; // overflow suffix + delimiter length is 9
            while (trimPosition > 1)
            {
                if (parentId[trimPosition - 1] == '.' || parentId[trimPosition - 1] == '_')
                    break;
                trimPosition--;
            }

            //ParentId is not valid Request-Id, let's generate proper one.
            if (trimPosition == 1)
                return GenerateRootId();

            //generate overflow suffix
            string overflowSuffix = ((int)GetRandomNumber()).ToString("x8");
            return parentId.Substring(0, trimPosition) + overflowSuffix + '#';
        }

        private string GenerateRootId()
        {
            // It is important that the part that changes frequently be first, because
            // many hash functions don't 'randomize' the tail of a string.   This makes
            // sampling based on the hash produce poor samples.
            return '|' + Interlocked.Increment(ref s_currentRootId).ToString("x") + s_uniqSuffix;
        }
#if ALLOW_PARTIALLY_TRUSTED_CALLERS
        [SecuritySafeCritical]
#endif
        private static unsafe long GetRandomNumber()
        {
            // Use the first 8 bytes of the GUID as a random number.  
            Guid g = Guid.NewGuid();
            return *((long*)&g);
        }

        private static bool ValidateSetCurrent(Activity activity)
        {
            bool canSet = activity == null || (activity.Id != null && !activity.isFinished);
            if (!canSet)
            {
                NotifyError(new InvalidOperationException("Trying to set an Activity that is not running"));
            }

            return canSet;
        }

        private string _rootId;
        private int _currentChildId;  // A unique number for all children of this activity.  

        // Used to generate an ID it represents the machine and process we are in.  
        private static readonly string s_uniqSuffix = "-" + GetRandomNumber().ToString("x") + ".";

        //A unique number inside the appdomain, randomized between appdomains. 
        //Int gives enough randomization and keeps hex-encoded s_currentRootId 8 chars long for most applications
        private static long s_currentRootId = (uint)GetRandomNumber();

        private const int RequestIdMaxLength = 1024;

        /// <summary>
        /// Having our own key-value linked list allows us to be more efficient  
        /// </summary>
        private partial class KeyValueListNode
        {
            public KeyValuePair<string, string> keyValue;
            public KeyValueListNode Next;
        }

        private static ActivityIdFormat s_DefaultIdFormat;
        private static Sampling s_sampling = new Sampling();

        private KeyValueListNode _tags;
        private KeyValueListNode _baggage;
        private string _traceState;
        private bool isFinished;
        #endregion // private
    }

    /// <summary>
    /// The possibilities for the format of the ID
    /// </summary>
    public enum ActivityIdFormat : byte
    {
        Unknown,      // ID format is not known.     
        Hierarchical, //|XXXX.XX.X_X ... see https://github.com/dotnet/corefx/blob/master/src/System.Diagnostics.DiagnosticSource/src/ActivityUserGuide.md#id-format
        W3C,          // 00-XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX-XXXXXXXXXXXXXXXX-XX see https://w3c.github.io/trace-context/
    };


    class Sampling
    {
        public Sampling()
        {
            _sampleIfLessEq = 0xFFFF;   // Set to 100% sampling by default.  
        }

        public bool IsActivitySampledEnabled { get; set; }

        /// <summary>
        /// Activities without an in-process parent (either because they are a root activity or 
        /// their parent is off machine (they only have an ID), are sampled at this Percentage.
        /// Thus if this is 5, then it means 5 out of every 100 non-parented activity will have its
        /// 'Recording' property turned on.    The smallest this can be is  .0015 which means 
        /// only 1 out of every 64K non-parented activities will be sampled.   
        /// </summary>
        public double SamplingPercent
        {
            get { return _sampleIfLessEq / (double)0XFFFF; }
        }

        public void RegisterSamplingConfig(ActivityConfig config)
        {
            if (config._next != null)
                throw new InvalidOperationException("Can only register sampling configuration once");
            config._next = _configs;
            _configs = config;

            if (config.SamplingPercent < SamplingPercent)
                SetSamplingPercent(config.SamplingPercent);
        }

        #region private 
        private void SetSamplingPercent(double value)
        {
            if (100 <= value)
                _sampleIfLessEq = 0xFFFF;
            else if (value < 0)
                _sampleIfLessEq = 0;
            else
                _sampleIfLessEq = (ushort)((value * 0xFFFF) / 100.0);
        }

        internal ushort NextHash()
        {
            int oldValue = _defaultHash;
            return (ushort)Interlocked.CompareExchange(ref _defaultHash, oldValue, oldValue * 214013 + 2531011);
        }

        internal bool ShouldSample(bool recordingDesired, ushort activityIdHash)
        {
            if (activityIdHash <= _sampleIfLessEq)
            {
                if (_samplesForExplicitRecording <= 0)
                {
                    if (recordingDesired)
                        return true;
                    else
                        Interlocked.Increment(ref _samplesForExplicitRecording);
                }
                else
                    return true;
            }

            if (recordingDesired && 0 < _samplesForExplicitRecording)
            {
                Interlocked.Decrement(ref _samplesForExplicitRecording);
                return true;
            }
            return false;
        }

        internal void Remove(ActivityConfig config)
        {
            // TODO NOT DONE.  
            // Search for entry, and compute the new sampling rate.  
        }

        ushort _sampleIfLessEq;
        int _samplesForExplicitRecording;
        int _defaultHash;
        ActivityConfig _configs;    // This is a linked list of registrations.  
        #endregion
    }


    public sealed class ActivityConfig : IDisposable
    {
        public ActivityConfig(double samplingPercent)
        {
            SamplingPercent = samplingPercent;
        }

        public double SamplingPercent { get; set; }

        ~ActivityConfig()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_owner != null)
                _owner.Remove(this);
        }

        Sampling _owner;
        internal ActivityConfig _next;   // next config in the list;
    }
}
