﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.ML.Runtime;
using Microsoft.ML.Trainers.FastTree;

namespace Microsoft.ML.Trainers.LightGbm
{
    /// <summary>
    /// Wrapper of Booster object of LightGBM.
    /// </summary>
    internal sealed class Booster : IDisposable
    {
        private readonly bool _hasValid;
        private readonly bool _hasMetric;

        public WrappedLightGbmInterface.SafeBoosterHandle Handle { get; private set; }
        public int BestIteration { get; set; }

        public Booster(Dictionary<string, object> parameters, Dataset trainset, Dataset validset = null)
        {
            var param = LightGbmInterfaceUtils.JoinParameters(parameters);
            LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterCreate(trainset.Handle, param, out var handle));
            Handle = handle;
            if (validset != null)
            {
                LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterAddValidData(Handle, validset.Handle));
                _hasValid = true;
            }

            int numEval = 0;
            BestIteration = -1;
            LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterGetEvalCounts(Handle, ref numEval));
            // At most one metric in ML.NET.
            Contracts.Assert(numEval <= 1);
            if (numEval == 1)
                _hasMetric = true;
        }

        public bool Update()
        {
            int isFinished = 0;
            LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterUpdateOneIter(Handle, ref isFinished));
            return isFinished == 1;
        }

        public double EvalTrain()
        {
            return Eval(0);
        }

        public double EvalValid()
        {
            if (_hasValid)
                return Eval(1);
            else
                return double.NaN;
        }

        private unsafe double Eval(int dataIdx)
        {
            if (!_hasMetric)
                return double.NaN;
            int outLen = 0;
            double[] res = new double[1];
            fixed (double* ptr = res)
                LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterGetEval(Handle, dataIdx, ref outLen, ptr));
            return res[0];
        }

        [BestFriend]
        internal unsafe string GetModelString()
        {
            int bufLen = 2 << 15;
            byte[] buffer = new byte[bufLen];
            int size = 0;
            fixed (byte* ptr = buffer)
                LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterSaveModelToString(Handle, 0, BestIteration, bufLen, ref size, ptr));
            // If buffer size is not enough, reallocate buffer and get again.
            if (size > bufLen)
            {
                bufLen = size;
                buffer = new byte[bufLen];
                fixed (byte* ptr = buffer)
                    LightGbmInterfaceUtils.Check(WrappedLightGbmInterface.BoosterSaveModelToString(Handle, 0, BestIteration, bufLen, ref size, ptr));
            }
            byte[] content = new byte[size];
            Array.Copy(buffer, content, size);
            fixed (byte* ptr = content)
                return LightGbmInterfaceUtils.GetString((IntPtr)ptr);
        }

        private static double[] Str2DoubleArray(string str, char delimiter)
        {
            var values = new List<double>();
            foreach (var token in str.Split(delimiter))
            {
                var trimmed = token.Trim().ToLowerInvariant();

                if (trimmed.Contains("-inf"))
                    values.Add(double.NegativeInfinity);
                else if (trimmed.Contains("inf"))
                    values.Add(double.PositiveInfinity);
                else if (trimmed.Contains("nan"))
                    values.Add(double.NaN);
                else
                    // The value carried in the trimmed string is not inf, -inf, or nan.
                    // Therefore, double.Parse should be able to generate a valid number from it.
                    // If parsing fails, an exception will be thrown.
                    values.Add(double.Parse(trimmed, CultureInfo.InvariantCulture));
            }
            return values.ToArray();
        }

        private static int[] Str2IntArray(string str, char delimiter)
        {
            return str.Split(delimiter).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        private static UInt32[] Str2UIntArray(string str, char delimiter)
        {
            return str.Split(delimiter).Select(x => UInt32.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }

        private static bool GetIsDefaultLeft(UInt32 decisionType)
        {
            // The second bit.
            return (decisionType & 2) > 0;
        }

        private static bool GetIsCategoricalSplit(UInt32 decisionType)
        {
            // The first bit.
            return (decisionType & 1) > 0;
        }

        private static bool GetHasMissing(UInt32 decisionType)
        {
            // The 3rd and 4th bits.
            return ((decisionType >> 2) & 3) > 0;
        }

        private static double[] GetDefalutValue(double[] threshold, UInt32[] decisionType)
        {
            double[] ret = new double[threshold.Length];
            for (int i = 0; i < threshold.Length; ++i)
            {
                if (GetHasMissing(decisionType[i]) && !GetIsCategoricalSplit(decisionType[i]))
                {
                    if (GetIsDefaultLeft(decisionType[i]))
                        ret[i] = threshold[i];
                    else
                        ret[i] = threshold[i] + 1;
                }
            }
            return ret;
        }

        private static bool FindInBitset(UInt32[] bits, int start, int end, int pos)
        {
            int i1 = pos / 32;
            if (start + i1 >= end)
                return false;
            int i2 = pos % 32;
            return ((bits[start + i1] >> i2) & 1) > 0;
        }

        private static int[] GetCatThresholds(UInt32[] catThreshold, int lowerBound, int upperBound)
        {
            List<int> cats = new List<int>();
            for (int j = lowerBound; j < upperBound; ++j)
            {
                // 32 bits.
                for (int k = 0; k < 32; ++k)
                {
                    int cat = (j - lowerBound) * 32 + k;
                    if (FindInBitset(catThreshold, lowerBound, upperBound, cat))
                        cats.Add(cat);
                }
            }
            return cats.ToArray();
        }

        public static int GetNumFeatures(string modelString)
        {
            string[] lines = modelString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            // Jump to the "max_feature_idx" value in the file. It's at the beginning.
            int i = 0;
            while (!lines[i].StartsWith("max_feature_idx"))
                i++;

            // Stored 0 based in the file, need the actual count so adding 1.
            return int.Parse(lines[i].Split('=')[1]) + 1;
        }

        public static Dictionary<string, string> GetParameters(string modelString)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>();
            string[] lines = modelString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Jump to the parameters section in the file. It's at the very end.
            int i = 0;
            while (!lines[i].StartsWith("parameters"))
                i++;

            // Increment once more to get to the first parameter
            i++;

            while (i <= lines.Length && !lines[i].StartsWith("end of parameters"))
            {
                var param = lines[i].Substring(1, lines[i].Length - 2).Split(':');
                parameters[param[0]] = param[1].Trim();
                i++;
            }

            return parameters;
        }

        public static InternalTreeEnsemble GetModel(int[] categoricalFeatureBoundaries, string modelString)
        {
            InternalTreeEnsemble res = new InternalTreeEnsemble();
            string[] lines = modelString.Split('\n');
            int i = 0;
            for (; i < lines.Length;)
            {
                if (lines[i].StartsWith("Tree="))
                {
                    Dictionary<string, string> kvPairs = new Dictionary<string, string>();
                    ++i;
                    while (!lines[i].StartsWith("Tree=") && lines[i].Trim().Length != 0)
                    {
                        string[] kv = lines[i].Split('=');
                        Contracts.Check(kv.Length == 2);
                        kvPairs[kv[0].Trim()] = kv[1].Trim();
                        ++i;
                    }
                    int numberOfLeaves = int.Parse(kvPairs["num_leaves"], CultureInfo.InvariantCulture);
                    int numCat = int.Parse(kvPairs["num_cat"], CultureInfo.InvariantCulture);
                    if (numberOfLeaves > 1)
                    {
                        var leftChild = Str2IntArray(kvPairs["left_child"], ' ');
                        var rightChild = Str2IntArray(kvPairs["right_child"], ' ');
                        var splitFeature = Str2IntArray(kvPairs["split_feature"], ' ');
                        var threshold = Str2DoubleArray(kvPairs["threshold"], ' ');
                        var splitGain = Str2DoubleArray(kvPairs["split_gain"], ' ');
                        var leafOutput = Str2DoubleArray(kvPairs["leaf_value"], ' ');
                        var decisionType = Str2UIntArray(kvPairs["decision_type"], ' ');
                        var defaultValue = GetDefalutValue(threshold, decisionType);
                        var categoricalSplitFeatures = new int[numberOfLeaves - 1][];
                        var categoricalSplit = new bool[numberOfLeaves - 1];
                        if (categoricalFeatureBoundaries != null)
                        {
                            // Add offsets to split features.
                            for (int node = 0; node < numberOfLeaves - 1; ++node)
                                splitFeature[node] = categoricalFeatureBoundaries[splitFeature[node]];
                        }

                        if (numCat > 0)
                        {
                            var catBoundaries = Str2IntArray(kvPairs["cat_boundaries"], ' ');
                            var catThreshold = Str2UIntArray(kvPairs["cat_threshold"], ' ');
                            for (int node = 0; node < numberOfLeaves - 1; ++node)
                            {
                                if (GetIsCategoricalSplit(decisionType[node]))
                                {
                                    int catIdx = (int)threshold[node];
                                    var cats = GetCatThresholds(catThreshold, catBoundaries[catIdx], catBoundaries[catIdx + 1]);
                                    categoricalSplitFeatures[node] = new int[cats.Length];
                                    // Convert Cat thresholds to feature indices.
                                    for (int j = 0; j < cats.Length; ++j)
                                        categoricalSplitFeatures[node][j] = splitFeature[node] + cats[j];

                                    splitFeature[node] = -1;
                                    categoricalSplit[node] = true;
                                    // Swap left and right child.
                                    int t = leftChild[node];
                                    leftChild[node] = rightChild[node];
                                    rightChild[node] = t;
                                }
                                else
                                {
                                    categoricalSplit[node] = false;
                                }
                            }
                        }
                        InternalRegressionTree tree = InternalRegressionTree.Create(numberOfLeaves, splitFeature, splitGain,
                            threshold.Select(x => (float)(x)).ToArray(), defaultValue.Select(x => (float)(x)).ToArray(), leftChild, rightChild, leafOutput,
                            categoricalSplitFeatures, categoricalSplit);
                        res.AddTree(tree);
                    }
                    else
                    {
                        InternalRegressionTree tree = new InternalRegressionTree(2);
                        var leafOutput = Str2DoubleArray(kvPairs["leaf_value"], ' ');
                        if (leafOutput[0] != 0)
                        {
                            // Convert Constant tree to Two-leaf tree, avoid being filter by TLC.
                            var categoricalSplitFeatures = new int[1][];
                            var categoricalSplit = new bool[1];
                            tree = InternalRegressionTree.Create(2, new int[] { 0 }, new double[] { 0 },
                                new float[] { 0 }, new float[] { 0 }, new int[] { -1 }, new int[] { -2 }, new double[] { leafOutput[0], leafOutput[0] },
                                categoricalSplitFeatures, categoricalSplit);
                        }
                        res.AddTree(tree);
                    }
                }
                else
                    ++i;
            }
            return res;
        }
        #region IDisposable Support
        public void Dispose()
        {
            Handle?.Dispose();
            Handle = null;
        }
        #endregion
    }
}
