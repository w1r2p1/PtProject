﻿using PtProject.Domain.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PtProject.Classifier
{
    [Serializable]
    public class DecisionTree
    {
        public static int N;
        private static readonly object Locker = new object();

        public alglib.decisionforest AlglibTree { get; private set; }
        public int NClasses { get; private set; }
        public int NVars { get; private set; }
        public int ModNvars { get; private set; }
        public int[] VarIndexes { get; private set; }
        public int[] RowIndexes { get; private set; }

        public int Id;

        /// <summary>
        /// Predict probability by exact tree (0 or 1)
        /// </summary>
        /// <param name="sarr">object coeffs to classify</param>
        /// <returns></returns>
        public double[] PredictCounts(double[] sarr)
        {
            if (sarr.Length != NVars)
                throw new InvalidOperationException("Error: NVars != sarr.Length ("+sarr.Length + "!=" + NVars+"); exiting...");

            double[] sy;

            try
            {
                sy = new double[NClasses];
                double[] nsarr = sarr;
                if (VarIndexes != null)
                {
                    nsarr = new double[VarIndexes == null ? NClasses : ModNvars];
                    for (int i = 0; i < ModNvars; i++)
                        nsarr[i] = sarr[VarIndexes[i]];
                }
                alglib.dfprocess(AlglibTree, nsarr, ref sy);
            }
            catch (Exception e)
            {
                Logger.Log(e);
                throw;
            }

            return sy;
        }

        /// <summary>
        /// creates tree by all xy array and all variables
        /// only internal use
        /// </summary>
        /// <param name="xy">train set</param>
        /// <param name="nclasses">now works only with 2</param>
        /// <param name="nvars"></param>
        /// <param name="npoints"></param>
        /// <param name="modNvars"></param>
        /// <param name="vidxes"></param>
        /// <returns></returns>
        private static DecisionTree CreateTree(double[,] xy, int nclasses, int nvars, int npoints, int modNvars, int[] vidxes, int[] ridxes)
        {
            DecisionTree result;

            try
            {
                int info;
                alglib.decisionforest df;
                alglib.dfreport rep;
                alglib.dfbuildrandomdecisionforest(xy, npoints, modNvars, nclasses, 1, 1, out info, out df, out rep);

                result = new DecisionTree();
                result.Id = CreateId();
                result.AlglibTree = df;
                result.NClasses = nclasses;
                result.NVars = nvars;
                result.ModNvars = modNvars;
                result.VarIndexes = vidxes;
                result.RowIndexes = ridxes;
            }
            catch (Exception e)
            {
                Logger.Log(e);
                throw;
            }

            return result;
        }

        public static DecisionTree CreateTree(int[] indexes, double[,] xy, int nclasses, double pcoeff, double vcoeff)
        {
            if (xy == null)
                throw new ArgumentException("xy is null", nameof(xy));

            int npoints = xy.GetLength(0);
            int nvars = xy.GetLength(1)-1;

            int modNpoints = (int)(npoints * pcoeff); // столько значений надо нагенерировать
            int modNvars = (int)(nvars * vcoeff); // столько переменных используем

            if (modNvars < 1)
                throw new ArgumentException("vcoeff too small", nameof(vcoeff));

            int[] vidxes = Enumerable.Range(0, nvars).ToArray();
            if (modNvars < nvars)
                vidxes = Enumerable.Range(0, nvars).OrderBy(c=>RandomGen.GetDouble()).Take(modNvars).ToArray();

            double[,] nxy = new double[modNpoints, modNvars + 1]; // сами значения

            int nk = 0; // столько нагенерировали
            var exists = new Dictionary<int, int>();

            if (indexes == null)
            {
                // basic tree
                while (nk < modNpoints)
                {
                    for (int i = 0; i < modNpoints; i++)
                    {
                        int sn = (int)(RandomGen.GetDouble() * npoints); // selection distribution
                        if (sn >= npoints) continue;

                        if (exists.ContainsKey(sn)) continue; // такой ключ уже был

                        exists.Add(sn, 0);
                        for (int j = 0; j < modNvars; j++)
                            nxy[i, j] = xy[sn, vidxes[j]];
                        nxy[i, modNvars] = xy[sn, nvars];
                        nk++;

                        if (nk >= modNpoints) break;
                    }

                    if (nk >= modNpoints) break;
                }
            }
            else
            {
                // tree, with distribution selection

                while (nk < modNpoints)
                {
                    for (int i = 0; i < modNpoints; i++)
                    {
                        int sn = (int)(RandomGen.GetTrangle() * npoints); // selection distribution
                        if (sn >= indexes.Length) continue;

                        if (exists.ContainsKey(sn)) continue; // такой ключ уже был

                        exists.Add(sn, 0);
                        int sidx = indexes[sn];
                        for (int j = 0; j < modNvars; j++)
                            nxy[i, j] = xy[sidx, vidxes[j]];
                        nxy[i, modNvars] = xy[sidx, nvars];
                        nk++;

                        if (nk >= modNpoints) break;
                    }

                    if (nk >= modNpoints) break;
                }
            }

            var ridxes = exists.Keys.ToArray();

            return CreateTree(nxy, nclasses, nvars, modNpoints, modNvars, vidxes, ridxes);
        }

        private static int CreateId()
        {
            int id;
            lock (Locker)
            {
                id = N++;
            }
            return id;
        }
    }
}
