using Lucene.Net.Documents;
using Lucene.Net.Randomized.Generators;
using Lucene.Net.Support;
using Lucene.Net.Support.Threading;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Console = Lucene.Net.Support.SystemConsole;

namespace Lucene.Net.Index
{
    using IBits = Lucene.Net.Util.IBits;
    using BytesRef = Lucene.Net.Util.BytesRef;

    /*
    * Licensed to the Apache Software Foundation (ASF) under one or more
    * contributor license agreements.  See the NOTICE file distributed with
    * this work for additional information regarding copyright ownership.
    * The ASF licenses this file to You under the Apache License, Version 2.0
    * (the "License"); you may not use this file except in compliance with
    * the License.  You may obtain a copy of the License at
    *
    *     http://www.apache.org/licenses/LICENSE-2.0
    *
    * Unless required by applicable law or agreed to in writing, software
    * distributed under the License is distributed on an "AS IS" BASIS,
    * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    * See the License for the specific language governing permissions and
    * limitations under the License.
    */

    using Codec = Lucene.Net.Codecs.Codec;
    using Constants = Lucene.Net.Util.Constants;
    using Directory = Lucene.Net.Store.Directory;
    using Document = Documents.Document;
    //using DocValuesType = Lucene.Net.Index.DocValuesType;
    using Field = Field;
    using FieldsConsumer = Lucene.Net.Codecs.FieldsConsumer;
    using FieldsProducer = Lucene.Net.Codecs.FieldsProducer;
    using FieldType = FieldType;
    using FixedBitSet = Lucene.Net.Util.FixedBitSet;
    using FlushInfo = Lucene.Net.Store.FlushInfo;
    using IOContext = Lucene.Net.Store.IOContext;
    using PostingsConsumer = Lucene.Net.Codecs.PostingsConsumer;
    using TermsConsumer = Lucene.Net.Codecs.TermsConsumer;
    using TermStats = Lucene.Net.Codecs.TermStats;
    using TestUtil = Lucene.Net.Util.TestUtil;

    /// <summary>
    /// Abstract class to do basic tests for a <see cref="Codecs.PostingsFormat"/>.
    /// NOTE: this test focuses on the postings
    /// (docs/freqs/positions/payloads/offsets) impl, not the
    /// terms dict.  The [stretch] goal is for this test to be
    /// so thorough in testing a new <see cref="Codecs.PostingsFormat"/> that if this
    /// test passes, then all Lucene/Solr tests should also pass.  Ie,
    /// if there is some bug in a given <see cref="Codecs.PostingsFormat"/> that this
    /// test fails to catch then this test needs to be improved!
    /// </summary>

    // TODO can we make it easy for testing to pair up a "random terms dict impl" with your postings base format...

    // TODO test when you reuse after skipping a term or two, eg the block reuse case

    /* TODO
      - threads
      - assert doc=-1 before any nextDoc
      - if a PF passes this test but fails other tests then this
        test has a bug!!
      - test tricky reuse cases, eg across fields
      - verify you get null if you pass needFreq/needOffset but
        they weren't indexed
    */

    [TestFixture]
    public abstract class BasePostingsFormatTestCase : BaseIndexFileFormatTestCase
    {
        private enum Option
        {
            // Sometimes use .Advance():
            SKIPPING,

            // Sometimes reuse the Docs/AndPositionsEnum across terms:
            REUSE_ENUMS,

            // Sometimes pass non-null live docs:
            LIVE_DOCS,

            // Sometimes seek to term using previously saved TermState:
            TERM_STATE,

            // Sometimes don't fully consume docs from the enum
            PARTIAL_DOC_CONSUME,

            // Sometimes don't fully consume positions at each doc
            PARTIAL_POS_CONSUME,

            // Sometimes check payloads
            PAYLOADS,

            // Test w/ multiple threads
            THREADS
        }

        /// <summary>
        /// Given the same random seed this always enumerates the
        /// same random postings.
        /// </summary>
        private class SeedPostings : DocsAndPositionsEnum
        {
            // Used only to generate docIDs; this way if you pull w/
            // or w/o positions you get the same docID sequence:
            internal readonly Random docRandom;

            internal readonly Random random;
            public int DocFreq { get; set; }
            internal readonly int maxDocSpacing;
            internal readonly int payloadSize;
            internal readonly bool fixedPayloads;
            internal readonly IBits liveDocs;
            internal readonly BytesRef payload;
            internal readonly IndexOptions options;
            internal readonly bool doPositions;

            internal int docID;
            internal int freq;
            public int Upto { get; set; }

            internal int pos;
            internal int offset;
            internal int startOffset;
            internal int endOffset;
            internal int posSpacing;
            internal int posUpto;

            public SeedPostings(long seed, int minDocFreq, int maxDocFreq, IBits liveDocs, IndexOptions options)
            {
                random = new Random((int)seed);
                docRandom = new Random(random.Next());
                DocFreq = TestUtil.NextInt32(random, minDocFreq, maxDocFreq);
                this.liveDocs = liveDocs;

                // TODO: more realistic to inversely tie this to numDocs:
                maxDocSpacing = TestUtil.NextInt32(random, 1, 100);

                if (random.Next(10) == 7)
                {
                    // 10% of the time create big payloads:
                    payloadSize = 1 + random.Next(3);
                }
                else
                {
                    payloadSize = 1 + random.Next(1);
                }

                fixedPayloads = random.NextBoolean();
                var payloadBytes = new byte[payloadSize];
                payload = new BytesRef(payloadBytes);
                this.options = options;
                doPositions = IndexOptions.DOCS_AND_FREQS_AND_POSITIONS.CompareTo(options) <= 0;
            }

            public override int NextDoc()
            {
                while (true)
                {
                    _nextDoc();
                    if (liveDocs == null || docID == NO_MORE_DOCS || liveDocs.Get(docID))
                    {
                        return docID;
                    }
                }
            }

            internal virtual int _nextDoc()
            {
                // Must consume random:
                while (posUpto < freq)
                {
                    NextPosition();
                }

                if (Upto < DocFreq)
                {
                    if (Upto == 0 && docRandom.NextBoolean())
                    {
                        // Sometimes index docID = 0
                    }
                    else if (maxDocSpacing == 1)
                    {
                        docID++;
                    }
                    else
                    {
                        // TODO: sometimes have a biggish gap here!
                        docID += TestUtil.NextInt32(docRandom, 1, maxDocSpacing);
                    }

                    if (random.Next(200) == 17)
                    {
                        freq = TestUtil.NextInt32(random, 1, 1000);
                    }
                    else if (random.Next(10) == 17)
                    {
                        freq = TestUtil.NextInt32(random, 1, 20);
                    }
                    else
                    {
                        freq = TestUtil.NextInt32(random, 1, 4);
                    }

                    pos = 0;
                    offset = 0;
                    posUpto = 0;
                    posSpacing = TestUtil.NextInt32(random, 1, 100);

                    Upto++;
                    return docID;
                }
                else
                {
                    return docID = NO_MORE_DOCS;
                }
            }

            public override int DocID
            {
                get { return docID; }
            }

            public override int Freq
            {
                get { return freq; }
            }

            public override int NextPosition()
            {
                if (!doPositions)
                {
                    posUpto = freq;
                    return 0;
                }
                Debug.Assert(posUpto < freq);

                if (posUpto == 0 && random.NextBoolean())
                {
                    // Sometimes index pos = 0
                }
                else if (posSpacing == 1)
                {
                    pos++;
                }
                else
                {
                    pos += TestUtil.NextInt32(random, 1, posSpacing);
                }

                if (payloadSize != 0)
                {
                    if (fixedPayloads)
                    {
                        payload.Length = payloadSize;
                        random.NextBytes(payload.Bytes);
                    }
                    else
                    {
                        int thisPayloadSize = random.Next(payloadSize);
                        if (thisPayloadSize != 0)
                        {
                            payload.Length = payloadSize;
                            random.NextBytes(payload.Bytes);
                        }
                        else
                        {
                            payload.Length = 0;
                        }
                    }
                }
                else
                {
                    payload.Length = 0;
                }

                startOffset = offset + random.Next(5);
                endOffset = startOffset + random.Next(10);
                offset = endOffset;

                posUpto++;
                return pos;
            }

            public override int StartOffset
            {
                get { return startOffset; }
            }

            public override int EndOffset
            {
                get { return endOffset; }
            }

            public override BytesRef GetPayload()
            {
                return payload.Length == 0 ? null : payload;
            }

            public override int Advance(int target)
            {
                return SlowAdvance(target);
            }

            public override long GetCost()
            {
                return DocFreq;
            }
        }

        private class FieldAndTerm
        {
            internal string Field { get; set; }
            internal BytesRef Term { get; set; }

            public FieldAndTerm(string field, BytesRef term)
            {
                this.Field = field;
                this.Term = BytesRef.DeepCopyOf(term);
            }
        }

        // Holds all postings:
        private static SortedDictionary<string, SortedDictionary<BytesRef, long>> fields;

        private static FieldInfos fieldInfos;

        private static FixedBitSet globalLiveDocs;

        private static IList<FieldAndTerm> allTerms;
        private static int maxDoc;

        private static long totalPostings;
        private static long totalPayloadBytes;

        private static SeedPostings GetSeedPostings(string term, long seed, bool withLiveDocs, IndexOptions options)
        {
            int minDocFreq, maxDocFreq;
            if (term.StartsWith("big_", StringComparison.Ordinal))
            {
                minDocFreq = RANDOM_MULTIPLIER * 50000;
                maxDocFreq = RANDOM_MULTIPLIER * 70000;
            }
            else if (term.StartsWith("medium_", StringComparison.Ordinal))
            {
                minDocFreq = RANDOM_MULTIPLIER * 3000;
                maxDocFreq = RANDOM_MULTIPLIER * 6000;
            }
            else if (term.StartsWith("low_", StringComparison.Ordinal))
            {
                minDocFreq = RANDOM_MULTIPLIER;
                maxDocFreq = RANDOM_MULTIPLIER * 40;
            }
            else
            {
                minDocFreq = 1;
                maxDocFreq = 3;
            }

            return new SeedPostings(seed, minDocFreq, maxDocFreq, withLiveDocs ? globalLiveDocs : null, options);
        }

        [OneTimeSetUp]
        public override void BeforeClass() // Renamed from CreatePostings to ensure the base class setup is called before this one
        {
            base.BeforeClass();

            totalPostings = 0;
            totalPayloadBytes = 0;

            // LUCENENET specific: Use StringComparer.Ordinal to get the same ordering as Java
            fields = new SortedDictionary<string, SortedDictionary<BytesRef, long>>(StringComparer.Ordinal);

            int numFields = TestUtil.NextInt32(Random(), 1, 5);
            if (VERBOSE)
            {
                Console.WriteLine("TEST: " + numFields + " fields");
            }
            maxDoc = 0;

            FieldInfo[] fieldInfoArray = new FieldInfo[numFields];
            int fieldUpto = 0;
            while (fieldUpto < numFields)
            {
                string field = TestUtil.RandomSimpleString(Random());
                if (fields.ContainsKey(field))
                {
                    continue;
                }

                fieldInfoArray[fieldUpto] = new FieldInfo(field, true, fieldUpto, false, false, true, 
                                                        IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS,
                                                        DocValuesType.NONE, DocValuesType.NUMERIC, null);
                fieldUpto++;

                SortedDictionary<BytesRef, long> postings = new SortedDictionary<BytesRef, long>();
                fields[field] = postings;
                HashSet<string> seenTerms = new HashSet<string>();

                int numTerms;
                if (Random().Next(10) == 7)
                {
                    numTerms = AtLeast(50);
                }
                else
                {
                    numTerms = TestUtil.NextInt32(Random(), 2, 20);
                }

                for (int termUpto = 0; termUpto < numTerms; termUpto++)
                {
                    string term = TestUtil.RandomSimpleString(Random());
                    if (seenTerms.Contains(term))
                    {
                        continue;
                    }
                    seenTerms.Add(term);

                    if (TEST_NIGHTLY && termUpto == 0 && fieldUpto == 1)
                    {
                        // Make 1 big term:
                        term = "big_" + term;
                    }
                    else if (termUpto == 1 && fieldUpto == 1)
                    {
                        // Make 1 medium term:
                        term = "medium_" + term;
                    }
                    else if (Random().NextBoolean())
                    {
                        // Low freq term:
                        term = "low_" + term;
                    }
                    else
                    {
                        // Very low freq term (don't multiply by RANDOM_MULTIPLIER):
                        term = "verylow_" + term;
                    }

                    long termSeed = Random().NextInt64();
                    postings[new BytesRef(term)] = termSeed;

                    // NOTE: sort of silly: we enum all the docs just to
                    // get the maxDoc
                    DocsEnum docsEnum = GetSeedPostings(term, termSeed, false, IndexOptions.DOCS_ONLY);
                    int doc;
                    int lastDoc = 0;
                    while ((doc = docsEnum.NextDoc()) != DocsEnum.NO_MORE_DOCS)
                    {
                        lastDoc = doc;
                    }
                    maxDoc = Math.Max(lastDoc, maxDoc);
                }
            }

            fieldInfos = new FieldInfos(fieldInfoArray);

            // It's the count, not the last docID:
            maxDoc++;

            globalLiveDocs = new FixedBitSet(maxDoc);
            double liveRatio = Random().NextDouble();
            for (int i = 0; i < maxDoc; i++)
            {
                if (Random().NextDouble() <= liveRatio)
                {
                    globalLiveDocs.Set(i);
                }
            }

            allTerms = new List<FieldAndTerm>();
            foreach (KeyValuePair<string, SortedDictionary<BytesRef, long>> fieldEnt in fields)
            {
                string field = fieldEnt.Key;
                foreach (KeyValuePair<BytesRef, long> termEnt in fieldEnt.Value.EntrySet())
                {
                    allTerms.Add(new FieldAndTerm(field, termEnt.Key));
                }
            }

            if (VERBOSE)
            {
                Console.WriteLine("TEST: done init postings; " + allTerms.Count + " total terms, across " + fieldInfos.Count + " fields");
            }
        }

        [OneTimeTearDown]
        public override void AfterClass()
        {
            allTerms = null;
            fieldInfos = null;
            fields = null;
            globalLiveDocs = null;
            base.AfterClass();
        }

        // TODO maybe instead of @BeforeClass just make a single test run: build postings & index & test it?

        private FieldInfos currentFieldInfos;

        // maxAllowed = the "highest" we can index, but we will still
        // randomly index at lower IndexOption
        private FieldsProducer BuildIndex(Directory dir, IndexOptions maxAllowed, bool allowPayloads, bool alwaysTestMax)
        {
            Codec codec = GetCodec();
            SegmentInfo segmentInfo = new SegmentInfo(dir, Constants.LUCENE_MAIN_VERSION, "_0", maxDoc, false, codec, null);

            int maxIndexOption = Enum.GetValues(typeof(IndexOptions)).Cast<IndexOptions>().ToList().IndexOf(maxAllowed);
            if (VERBOSE)
            {
                Console.WriteLine("\nTEST: now build index");
            }

            int maxIndexOptionNoOffsets = Enum.GetValues(typeof(IndexOptions)).Cast<IndexOptions>().ToList().IndexOf(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS);

            // TODO use allowPayloads

            var newFieldInfoArray = new FieldInfo[fields.Count];
            for (int fieldUpto = 0; fieldUpto < fields.Count; fieldUpto++)
            {
                FieldInfo oldFieldInfo = fieldInfos.FieldInfo(fieldUpto);

                string pf = TestUtil.GetPostingsFormat(codec, oldFieldInfo.Name);
                int fieldMaxIndexOption;
                if (DoesntSupportOffsets.Contains(pf))
                {
                    fieldMaxIndexOption = Math.Min(maxIndexOptionNoOffsets, maxIndexOption);
                }
                else
                {
                    fieldMaxIndexOption = maxIndexOption;
                }

                // Randomly picked the IndexOptions to index this
                // field with:
                IndexOptions indexOptions = Enum.GetValues(typeof(IndexOptions)).Cast<IndexOptions>().ToArray()[alwaysTestMax ? fieldMaxIndexOption : Random().Next(1, 1 + fieldMaxIndexOption)]; // LUCENENET: Skipping NONE option
                bool doPayloads = indexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0 && allowPayloads;

                newFieldInfoArray[fieldUpto] = new FieldInfo(oldFieldInfo.Name, true, fieldUpto, false, false, doPayloads, indexOptions, DocValuesType.NONE, DocValuesType.NUMERIC, null);
            }

            FieldInfos newFieldInfos = new FieldInfos(newFieldInfoArray);

            // Estimate that flushed segment size will be 25% of
            // what we use in RAM:
            long bytes = totalPostings * 8 + totalPayloadBytes;

            SegmentWriteState writeState = new SegmentWriteState(null, dir, segmentInfo, newFieldInfos, 32, null, new IOContext(new FlushInfo(maxDoc, bytes)));

            // LUCENENET specific - BUG: we must wrap this in a using block in case anything in the below loop throws
            using (FieldsConsumer fieldsConsumer = codec.PostingsFormat.FieldsConsumer(writeState))
            {

                foreach (KeyValuePair<string, SortedDictionary<BytesRef, long>> fieldEnt in fields)
                {
                    string field = fieldEnt.Key;
                    IDictionary<BytesRef, long> terms = fieldEnt.Value;

                    FieldInfo fieldInfo = newFieldInfos.FieldInfo(field);

                    IndexOptions indexOptions = fieldInfo.IndexOptions;

                    if (VERBOSE)
                    {
                        Console.WriteLine("field=" + field + " indexOtions=" + indexOptions);
                    }

                    bool doFreq = indexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS) >= 0;
                    bool doPos = indexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0;
                    bool doPayloads = indexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0 && allowPayloads;
                    bool doOffsets = indexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS) >= 0;

                    TermsConsumer termsConsumer = fieldsConsumer.AddField(fieldInfo);
                    long sumTotalTF = 0;
                    long sumDF = 0;
                    FixedBitSet seenDocs = new FixedBitSet(maxDoc);
                    foreach (KeyValuePair<BytesRef, long> termEnt in terms)
                    {
                        BytesRef term = termEnt.Key;
                        SeedPostings postings = GetSeedPostings(term.Utf8ToString(), termEnt.Value, false, maxAllowed);
                        if (VERBOSE)
                        {
                            Console.WriteLine("  term=" + field + ":" + term.Utf8ToString() + " docFreq=" + postings.DocFreq + " seed=" + termEnt.Value);
                        }

                        PostingsConsumer postingsConsumer = termsConsumer.StartTerm(term);
                        long totalTF = 0;
                        int docID = 0;
                        while ((docID = postings.NextDoc()) != DocsEnum.NO_MORE_DOCS)
                        {
                            int freq = postings.Freq;
                            if (VERBOSE)
                            {
                                Console.WriteLine("    " + postings.Upto + ": docID=" + docID + " freq=" + postings.freq);
                            }
                            postingsConsumer.StartDoc(docID, doFreq ? postings.freq : -1);
                            seenDocs.Set(docID);
                            if (doPos)
                            {
                                totalTF += postings.freq;
                                for (int posUpto = 0; posUpto < freq; posUpto++)
                                {
                                    int pos = postings.NextPosition();
                                    BytesRef payload = postings.GetPayload();

                                    if (VERBOSE)
                                    {
                                        if (doPayloads)
                                        {
                                            Console.WriteLine("      pos=" + pos + " payload=" + (payload == null ? "null" : payload.Length + " bytes"));
                                        }
                                        else
                                        {
                                            Console.WriteLine("      pos=" + pos);
                                        }
                                    }
                                    postingsConsumer.AddPosition(pos, doPayloads ? payload : null, doOffsets ? postings.StartOffset : -1, doOffsets ? postings.EndOffset : -1);
                                }
                            }
                            else if (doFreq)
                            {
                                totalTF += freq;
                            }
                            else
                            {
                                totalTF++;
                            }
                            postingsConsumer.FinishDoc();
                        }
                        termsConsumer.FinishTerm(term, new TermStats(postings.DocFreq, doFreq ? totalTF : -1));
                        sumTotalTF += totalTF;
                        sumDF += postings.DocFreq;
                    }

                    termsConsumer.Finish(doFreq ? sumTotalTF : -1, sumDF, seenDocs.Cardinality());
                }

            }

            if (VERBOSE)
            {
                Console.WriteLine("TEST: after indexing: files=");
                foreach (string file in dir.ListAll())
                {
                    Console.WriteLine("  " + file + ": " + dir.FileLength(file) + " bytes");
                }
            }

            currentFieldInfos = newFieldInfos;

            SegmentReadState readState = new SegmentReadState(dir, segmentInfo, newFieldInfos, IOContext.READ, 1);

            return codec.PostingsFormat.FieldsProducer(readState);
        }

        private class ThreadState
        {
            // Only used with REUSE option:
            public DocsEnum ReuseDocsEnum { get; set; }

            public DocsAndPositionsEnum ReuseDocsAndPositionsEnum { get; set; }
        }

        private void VerifyEnum(ThreadState threadState, 
                                string field, 
                                BytesRef term, 
                                TermsEnum termsEnum,

                                // Maximum options (docs/freqs/positions/offsets) to test:
                                IndexOptions maxTestOptions, 
                                
                                IndexOptions maxIndexOptions, 
                                ISet<Option> options, 
                                bool alwaysTestMax)
        
        {
            if (VERBOSE)
            {
                Console.WriteLine("  verifyEnum: options=" + options + " maxTestOptions=" + maxTestOptions);
            }

            // Make sure TermsEnum really is positioned on the
            // expected term:
            Assert.AreEqual(term, termsEnum.Term);

            // 50% of the time time pass liveDocs:
            bool useLiveDocs = options.Contains(Option.LIVE_DOCS) && Random().NextBoolean();
            IBits liveDocs;
            if (useLiveDocs)
            {
                liveDocs = globalLiveDocs;
                if (VERBOSE)
                {
                    Console.WriteLine("  use liveDocs");
                }
            }
            else
            {
                liveDocs = null;
                if (VERBOSE)
                {
                    Console.WriteLine("  no liveDocs");
                }
            }

            FieldInfo fieldInfo = currentFieldInfos.FieldInfo(field);

            // NOTE: can be empty list if we are using liveDocs:
            SeedPostings expected = GetSeedPostings(term.Utf8ToString(), 
                                                    fields[field][term], 
                                                    useLiveDocs, 
                                                    maxIndexOptions);
            Assert.AreEqual(expected.DocFreq, termsEnum.DocFreq);

            bool allowFreqs = fieldInfo.IndexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS) >= 0 && 
                maxTestOptions.CompareTo(IndexOptions.DOCS_AND_FREQS) >= 0;
            bool doCheckFreqs = allowFreqs && (alwaysTestMax || Random().Next(3) <= 2);

            bool allowPositions = fieldInfo.IndexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0 && 
                maxTestOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS) >= 0;
            bool doCheckPositions = allowPositions && (alwaysTestMax || Random().Next(3) <= 2);

            bool allowOffsets = fieldInfo.IndexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS) >=0 && 
                maxTestOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS) >= 0;
            bool doCheckOffsets = allowOffsets && (alwaysTestMax || Random().Next(3) <= 2);

            bool doCheckPayloads = options.Contains(Option.PAYLOADS) && allowPositions && fieldInfo.HasPayloads && (alwaysTestMax || Random().Next(3) <= 2);

            DocsEnum prevDocsEnum = null;

            DocsEnum docsEnum;
            DocsAndPositionsEnum docsAndPositionsEnum;

            if (!doCheckPositions)
            {
                if (allowPositions && Random().Next(10) == 7)
                {
                    // 10% of the time, even though we will not check positions, pull a DocsAndPositions enum

                    if (options.Contains(Option.REUSE_ENUMS) && Random().Next(10) < 9)
                    {
                        prevDocsEnum = threadState.ReuseDocsAndPositionsEnum;
                    }

                    DocsAndPositionsFlags flags = 0;
                    if (alwaysTestMax || Random().NextBoolean())
                    {
                        flags |= DocsAndPositionsFlags.OFFSETS;
                    }
                    if (alwaysTestMax || Random().NextBoolean())
                    {
                        flags |= DocsAndPositionsFlags.PAYLOADS;
                    }

                    if (VERBOSE)
                    {
                        Console.WriteLine("  get DocsAndPositionsEnum (but we won't check positions) flags=" + flags);
                    }

                    threadState.ReuseDocsAndPositionsEnum = termsEnum.DocsAndPositions(liveDocs, (DocsAndPositionsEnum)prevDocsEnum, flags);
                    docsEnum = threadState.ReuseDocsAndPositionsEnum;
                    docsAndPositionsEnum = threadState.ReuseDocsAndPositionsEnum;
                }
                else
                {
                    if (VERBOSE)
                    {
                        Console.WriteLine("  get DocsEnum");
                    }
                    if (options.Contains(Option.REUSE_ENUMS) && Random().Next(10) < 9)
                    {
                        prevDocsEnum = threadState.ReuseDocsEnum;
                    }
                    threadState.ReuseDocsEnum = termsEnum.Docs(liveDocs, prevDocsEnum, doCheckFreqs ? DocsFlags.FREQS : DocsFlags.NONE);
                    docsEnum = threadState.ReuseDocsEnum;
                    docsAndPositionsEnum = null;
                }
            }
            else
            {
                if (options.Contains(Option.REUSE_ENUMS) && Random().Next(10) < 9)
                {
                    prevDocsEnum = threadState.ReuseDocsAndPositionsEnum;
                }

                DocsAndPositionsFlags flags = 0;
                if (alwaysTestMax || doCheckOffsets || Random().Next(3) == 1)
                {
                    flags |= DocsAndPositionsFlags.OFFSETS;
                }
                if (alwaysTestMax || doCheckPayloads || Random().Next(3) == 1)
                {
                    flags |= DocsAndPositionsFlags.PAYLOADS;
                }

                if (VERBOSE)
                {
                    Console.WriteLine("  get DocsAndPositionsEnum flags=" + flags);
                }

                threadState.ReuseDocsAndPositionsEnum = termsEnum.DocsAndPositions(liveDocs, (DocsAndPositionsEnum)prevDocsEnum, flags);
                docsEnum = threadState.ReuseDocsAndPositionsEnum;
                docsAndPositionsEnum = threadState.ReuseDocsAndPositionsEnum;
            }

            Assert.IsNotNull(docsEnum, "null DocsEnum");
            int initialDocID = docsEnum.DocID;
            Assert.AreEqual(-1, initialDocID, "inital docID should be -1" + docsEnum);

            if (VERBOSE)
            {
                if (prevDocsEnum == null)
                {
                    Console.WriteLine("  got enum=" + docsEnum);
                }
                else if (prevDocsEnum == docsEnum)
                {
                    Console.WriteLine("  got reuse enum=" + docsEnum);
                }
                else
                {
                    Console.WriteLine("  got enum=" + docsEnum + " (reuse of " + prevDocsEnum + " failed)");
                }
            }

            // 10% of the time don't consume all docs:
            int stopAt;
            if (!alwaysTestMax && options.Contains(Option.PARTIAL_DOC_CONSUME) && expected.DocFreq > 1 && Random().Next(10) == 7)
            {
                stopAt = Random().Next(expected.DocFreq - 1);
                if (VERBOSE)
                {
                    Console.WriteLine("  will not consume all docs (" + stopAt + " vs " + expected.DocFreq + ")");
                }
            }
            else
            {
                stopAt = expected.DocFreq;
                if (VERBOSE)
                {
                    Console.WriteLine("  consume all docs");
                }
            }

            double skipChance = alwaysTestMax ? 0.5 : Random().NextDouble();
            int numSkips = expected.DocFreq < 3 ? 1 : TestUtil.NextInt32(Random(), 1, Math.Min(20, expected.DocFreq / 3));
            int skipInc = expected.DocFreq / numSkips;
            int skipDocInc = maxDoc / numSkips;

            // Sometimes do 100% skipping:
            bool doAllSkipping = options.Contains(Option.SKIPPING) && Random().Next(7) == 1;

            double freqAskChance = alwaysTestMax ? 1.0 : Random().NextDouble();
            double payloadCheckChance = alwaysTestMax ? 1.0 : Random().NextDouble();
            double offsetCheckChance = alwaysTestMax ? 1.0 : Random().NextDouble();

            if (VERBOSE)
            {
                if (options.Contains(Option.SKIPPING))
                {
                    Console.WriteLine("  skipChance=" + skipChance + " numSkips=" + numSkips);
                }
                else
                {
                    Console.WriteLine("  no skipping");
                }
                if (doCheckFreqs)
                {
                    Console.WriteLine("  freqAskChance=" + freqAskChance);
                }
                if (doCheckPayloads)
                {
                    Console.WriteLine("  payloadCheckChance=" + payloadCheckChance);
                }
                if (doCheckOffsets)
                {
                    Console.WriteLine("  offsetCheckChance=" + offsetCheckChance);
                }
            }

            while (expected.Upto <= stopAt)
            {
                if (expected.Upto == stopAt)
                {
                    if (stopAt == expected.DocFreq)
                    {
                        Assert.AreEqual(DocsEnum.NO_MORE_DOCS, docsEnum.NextDoc(), "DocsEnum should have ended but didn't");

                        // Common bug is to forget to set this.Doc=NO_MORE_DOCS in the enum!:
                        Assert.AreEqual(DocsEnum.NO_MORE_DOCS, docsEnum.DocID, "DocsEnum should have ended but didn't");
                    }
                    break;
                }

                if (options.Contains(Option.SKIPPING) && (doAllSkipping || Random().NextDouble() <= skipChance))
                {
                    int targetDocID = -1;
                    if (expected.Upto < stopAt && Random().NextBoolean())
                    {
                        // Pick target we know exists:
                        int skipCount = TestUtil.NextInt32(Random(), 1, skipInc);
                        for (int skip = 0; skip < skipCount; skip++)
                        {
                            if (expected.NextDoc() == DocsEnum.NO_MORE_DOCS)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Pick random target (might not exist):
                        int skipDocIDs = TestUtil.NextInt32(Random(), 1, skipDocInc);
                        if (skipDocIDs > 0)
                        {
                            targetDocID = expected.DocID + skipDocIDs;
                            expected.Advance(targetDocID);
                        }
                    }

                    if (expected.Upto >= stopAt)
                    {
                        int target = Random().NextBoolean() ? maxDoc : DocsEnum.NO_MORE_DOCS;
                        if (VERBOSE)
                        {
                            Console.WriteLine("  now advance to end (target=" + target + ")");
                        }
                        Assert.AreEqual(DocsEnum.NO_MORE_DOCS, docsEnum.Advance(target), "DocsEnum should have ended but didn't");
                        break;
                    }
                    else
                    {
                        if (VERBOSE)
                        {
                            if (targetDocID != -1)
                            {
                                Console.WriteLine("  now advance to random target=" + targetDocID + " (" + expected.Upto + " of " + stopAt + ") current=" + docsEnum.DocID);
                            }
                            else
                            {
                                Console.WriteLine("  now advance to known-exists target=" + expected.DocID + " (" + expected.Upto + " of " + stopAt + ") current=" + docsEnum.DocID);
                            }
                        }
                        int docID = docsEnum.Advance(targetDocID != -1 ? targetDocID : expected.DocID);
                        Assert.AreEqual(expected.DocID, docID, "docID is wrong");
                    }
                }
                else
                {
                    expected.NextDoc();
                    if (VERBOSE)
                    {
                        Console.WriteLine("  now nextDoc to " + expected.DocID + " (" + expected.Upto + " of " + stopAt + ")");
                    }
                    int docID = docsEnum.NextDoc();
                    Assert.AreEqual(expected.DocID, docID, "docID is wrong");
                    if (docID == DocsEnum.NO_MORE_DOCS)
                    {
                        break;
                    }
                }

                if (doCheckFreqs && Random().NextDouble() <= freqAskChance)
                {
                    if (VERBOSE)
                    {
                        Console.WriteLine("    now freq()=" + expected.Freq);
                    }
                    int freq = docsEnum.Freq;
                    Assert.AreEqual(expected.Freq, freq, "freq is wrong");
                }

                if (doCheckPositions)
                {
                    int freq = docsEnum.Freq;
                    int numPosToConsume;
                    if (!alwaysTestMax && options.Contains(Option.PARTIAL_POS_CONSUME) && Random().Next(5) == 1)
                    {
                        numPosToConsume = Random().Next(freq);
                    }
                    else
                    {
                        numPosToConsume = freq;
                    }

                    for (int i = 0; i < numPosToConsume; i++)
                    {
                        int pos = expected.NextPosition();
                        if (VERBOSE)
                        {
                            Console.WriteLine("    now nextPosition to " + pos);
                        }
                        Assert.AreEqual(pos, docsAndPositionsEnum.NextPosition(), "position is wrong");

                        if (doCheckPayloads)
                        {
                            BytesRef expectedPayload = expected.GetPayload();
                            if (Random().NextDouble() <= payloadCheckChance)
                            {
                                if (VERBOSE)
                                {
                                    Console.WriteLine("      now check expectedPayload length=" + (expectedPayload == null ? 0 : expectedPayload.Length));
                                }
                                if (expectedPayload == null || expectedPayload.Length == 0)
                                {
                                    Assert.IsNull(docsAndPositionsEnum.GetPayload(), "should not have payload");
                                }
                                else
                                {
                                    BytesRef payload = docsAndPositionsEnum.GetPayload();
                                    Assert.IsNotNull(payload, "should have payload but doesn't");

                                    Assert.AreEqual(expectedPayload.Length, payload.Length, "payload length is wrong");
                                    for (int byteUpto = 0; byteUpto < expectedPayload.Length; byteUpto++)
                                    {
                                        Assert.AreEqual(expectedPayload.Bytes[expectedPayload.Offset + byteUpto], payload.Bytes[payload.Offset + byteUpto], "payload bytes are wrong");
                                    }

                                    // make a deep copy
                                    payload = BytesRef.DeepCopyOf(payload);
                                    Assert.AreEqual(payload, docsAndPositionsEnum.GetPayload(), "2nd call to getPayload returns something different!");
                                }
                            }
                            else
                            {
                                if (VERBOSE)
                                {
                                    Console.WriteLine("      skip check payload length=" + (expectedPayload == null ? 0 : expectedPayload.Length));
                                }
                            }
                        }

                        if (doCheckOffsets)
                        {
                            if (Random().NextDouble() <= offsetCheckChance)
                            {
                                if (VERBOSE)
                                {
                                    Console.WriteLine("      now check offsets: startOff=" + expected.StartOffset + " endOffset=" + expected.EndOffset);
                                }
                                Assert.AreEqual(expected.StartOffset, docsAndPositionsEnum.StartOffset, "startOffset is wrong");
                                Assert.AreEqual(expected.EndOffset, docsAndPositionsEnum.EndOffset, "endOffset is wrong");
                            }
                            else
                            {
                                if (VERBOSE)
                                {
                                    Console.WriteLine("      skip check offsets");
                                }
                            }
                        }
                        else if (fieldInfo.IndexOptions.CompareTo(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS) < 0)
                        {
                            if (VERBOSE)
                            {
                                Console.WriteLine("      now check offsets are -1");
                            }
                            Assert.AreEqual(-1, docsAndPositionsEnum.StartOffset, "startOffset isn't -1");
                            Assert.AreEqual(-1, docsAndPositionsEnum.EndOffset, "endOffset isn't -1");
                        }
                    }
                }
            }
        }

        new private class TestThread : ThreadClass
        {
            internal Fields fieldsSource;
            internal ISet<Option> options;
            internal IndexOptions maxIndexOptions;
            internal IndexOptions maxTestOptions;
            internal bool alwaysTestMax;
            internal BasePostingsFormatTestCase testCase;

            public TestThread(BasePostingsFormatTestCase testCase, Fields fieldsSource, ISet<Option> options, IndexOptions maxTestOptions, IndexOptions maxIndexOptions, bool alwaysTestMax)
            {
                this.fieldsSource = fieldsSource;
                this.options = options;
                this.maxTestOptions = maxTestOptions;
                this.maxIndexOptions = maxIndexOptions;
                this.alwaysTestMax = alwaysTestMax;
                this.testCase = testCase;
            }

            public override void Run()
            {
                try
                {
                    try
                    {
                        testCase.TestTermsOneThread(fieldsSource, options, maxTestOptions, maxIndexOptions, alwaysTestMax);
                    }
                    catch (Exception t)
                    {
                        throw new Exception(t.Message, t);
                    }
                }
                finally
                {
                    fieldsSource = null;
                    testCase = null;
                }
            }
        }

        private void TestTerms(Fields fieldsSource, ISet<Option> options, 
                                IndexOptions maxTestOptions, 
                                IndexOptions maxIndexOptions, 
                                bool alwaysTestMax)
        {
            if (options.Contains(Option.THREADS))
            {
                int numThreads = TestUtil.NextInt32(Random(), 2, 5);
                ThreadClass[] threads = new ThreadClass[numThreads];
                for (int threadUpto = 0; threadUpto < numThreads; threadUpto++)
                {
                    threads[threadUpto] = new TestThread(this, fieldsSource, options, maxTestOptions, maxIndexOptions, alwaysTestMax);
                    threads[threadUpto].Start();
                }
                for (int threadUpto = 0; threadUpto < numThreads; threadUpto++)
                {
                    threads[threadUpto].Join();
                }
            }
            else
            {
                TestTermsOneThread(fieldsSource, options, maxTestOptions, maxIndexOptions, alwaysTestMax);
            }
        }

        private void TestTermsOneThread(Fields fieldsSource, ISet<Option> options, 
                                        IndexOptions maxTestOptions, 
                                        IndexOptions maxIndexOptions, 
                                        bool alwaysTestMax)
        {
            ThreadState threadState = new ThreadState();

            // Test random terms/fields:
            IList<TermState> termStates = new List<TermState>();
            IList<FieldAndTerm> termStateTerms = new List<FieldAndTerm>();

            Collections.Shuffle(allTerms);
            int upto = 0;
            while (upto < allTerms.Count)
            {
                bool useTermState = termStates.Count != 0 && Random().Next(5) == 1;
                FieldAndTerm fieldAndTerm;
                TermsEnum termsEnum;

                TermState termState = null;

                if (!useTermState)
                {
                    // Seek by random field+term:
                    fieldAndTerm = allTerms[upto++];
                    if (VERBOSE)
                    {
                        Console.WriteLine("\nTEST: seek to term=" + fieldAndTerm.Field + ":" + fieldAndTerm.Term.Utf8ToString());
                    }
                }
                else
                {
                    // Seek by previous saved TermState
                    int idx = Random().Next(termStates.Count);
                    fieldAndTerm = termStateTerms[idx];
                    if (VERBOSE)
                    {
                        Console.WriteLine("\nTEST: seek using TermState to term=" + fieldAndTerm.Field + ":" + fieldAndTerm.Term.Utf8ToString());
                    }
                    termState = termStates[idx];
                }

                Terms terms = fieldsSource.GetTerms(fieldAndTerm.Field);
                Assert.IsNotNull(terms);
                termsEnum = terms.GetIterator(null);

                if (!useTermState)
                {
                    Assert.IsTrue(termsEnum.SeekExact(fieldAndTerm.Term));
                }
                else
                {
                    termsEnum.SeekExact(fieldAndTerm.Term, termState);
                }

                bool savedTermState = false;

                if (options.Contains(Option.TERM_STATE) && !useTermState && Random().Next(5) == 1)
                {
                    // Save away this TermState:
                    termStates.Add(termsEnum.GetTermState());
                    termStateTerms.Add(fieldAndTerm);
                    savedTermState = true;
                }

                VerifyEnum(threadState, 
                            fieldAndTerm.Field, 
                            fieldAndTerm.Term, 
                            termsEnum, 
                            maxTestOptions, 
                            maxIndexOptions, 
                            options, 
                            alwaysTestMax);

                // Sometimes save term state after pulling the enum:
                if (options.Contains(Option.TERM_STATE) && !useTermState && !savedTermState && Random().Next(5) == 1)
                {
                    // Save away this TermState:
                    termStates.Add(termsEnum.GetTermState());
                    termStateTerms.Add(fieldAndTerm);
                    useTermState = true;
                }

                // 10% of the time make sure you can pull another enum
                // from the same term:
                if (alwaysTestMax || Random().Next(10) == 7)
                {
                    // Try same term again
                    if (VERBOSE)
                    {
                        Console.WriteLine("TEST: try enum again on same term");
                    }

                    VerifyEnum(threadState, 
                                fieldAndTerm.Field, 
                                fieldAndTerm.Term, 
                                termsEnum, 
                                maxTestOptions, 
                                maxIndexOptions, 
                                options, 
                                alwaysTestMax);
                }
            }
        }

        private void TestFields(Fields fields)
        {
            IEnumerator<string> iterator = fields.GetEnumerator();
            while (iterator.MoveNext())
            {
                var dummy = iterator.Current;
                // .NET: Testing for iterator.Remove() isn't applicable
            }
            Assert.IsFalse(iterator.MoveNext());

            // .NET: Testing for NoSuchElementException with .NET iterators isn't applicable
        }

        /// <summary>
        /// Indexes all fields/terms at the specified
        /// <see cref="IndexOptions"/>, and fully tests at that <see cref="IndexOptions"/>.
        /// </summary>
        private void TestFull(IndexOptions options, bool withPayloads)
        {
            DirectoryInfo path = CreateTempDir("testPostingsFormat.testExact");
            using (Directory dir = NewFSDirectory(path))
            {
                // TODO test thread safety of buildIndex too
                var fieldsProducer = BuildIndex(dir, options, withPayloads, true);

                TestFields(fieldsProducer);

                var allOptions = ((IndexOptions[])Enum.GetValues(typeof(IndexOptions))).Skip(1).ToArray(); // LUCENENET: Skip our NONE option
                    //IndexOptions_e.values();
                int maxIndexOption = Arrays.AsList(allOptions).IndexOf(options);

                for (int i = 0; i <= maxIndexOption; i++)
                {
                    ISet<Option> allOptionsHashSet = new HashSet<Option>(Enum.GetValues(typeof (Option)).Cast<Option>());
                    TestTerms(fieldsProducer, allOptionsHashSet, allOptions[i], options, true);
                    if (withPayloads)
                    {
                        // If we indexed w/ payloads, also test enums w/o accessing payloads:
                        ISet<Option> payloadsHashSet = new HashSet<Option>() {Option.PAYLOADS};
                        var complementHashSet = new HashSet<Option>(allOptionsHashSet.Except(payloadsHashSet));
                        TestTerms(fieldsProducer, complementHashSet, allOptions[i], options, true);
                    }
                }

                fieldsProducer.Dispose();
            }
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsOnly()
        {
            TestFull(IndexOptions.DOCS_ONLY, false);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsAndFreqs()
        {
            TestFull(IndexOptions.DOCS_AND_FREQS, false);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsAndFreqsAndPositions()
        {
            TestFull(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS, false);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsAndFreqsAndPositionsAndPayloads()
        {
            TestFull(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS, true);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsAndFreqsAndPositionsAndOffsets()
        {
            TestFull(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS, false);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestDocsAndFreqsAndPositionsAndOffsetsAndPayloads()
        {
            TestFull(IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS, true);
        }

        // [Test] // LUCENENET NOTE: For now, we are overriding this test in every subclass to pull it into the right context for the subclass
        public virtual void TestRandom()
        {
            int iters = 5;

            for (int iter = 0; iter < iters; iter++)
            {
                DirectoryInfo path = CreateTempDir("testPostingsFormat");
                Directory dir = NewFSDirectory(path);

                bool indexPayloads = Random().NextBoolean();
                // TODO test thread safety of buildIndex too
                FieldsProducer fieldsProducer = BuildIndex(dir, IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS, indexPayloads, false);

                TestFields(fieldsProducer);

                // NOTE: you can also test "weaker" index options than
                // you indexed with:
                TestTerms(fieldsProducer,
                    // LUCENENET: Skip our NONE option
                    new HashSet<Option>(Enum.GetValues(typeof(Option)).Cast<Option>().Skip(1)), 
                    IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS, 
                    IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS, 
                    false);

                fieldsProducer.Dispose();
                fieldsProducer = null;

                dir.Dispose();
                System.IO.Directory.Delete(path.FullName, true);
            }
        }

        protected internal override void AddRandomFields(Document doc)
        {
            
            foreach (IndexOptions opts in Enum.GetValues(typeof(IndexOptions)))
            {
                // LUCENENET: Skip our NONE option
                if (opts == IndexOptions.NONE)
                {
                    continue;
                }

                string field = "f_" + opts;
                string pf = TestUtil.GetPostingsFormat(Codec.Default, field);
                if (opts == IndexOptions.DOCS_AND_FREQS_AND_POSITIONS_AND_OFFSETS && DoesntSupportOffsets.Contains(pf))
                {
                    continue;
                }
                var ft = new FieldType { IndexOptions = opts, IsIndexed = true, OmitNorms = true};
                ft.Freeze();
                int numFields = Random().Next(5);
                for (int j = 0; j < numFields; ++j)
                {
                    doc.Add(new Field("f_" + opts, TestUtil.RandomSimpleString(Random(), 2), ft));
                }
            }
        }
    }
}