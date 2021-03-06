﻿using PtProject.Domain.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using PtProject.Domain;

namespace PtProject.Classifier
{
    [Serializable]
    public class DecisionBatch
    {
        public static int NCls;

        /// <summary>
        /// List of decision trees
        /// </summary>
        private readonly List<DecisionTree> _batch;

        /// <summary>
        /// Classifier id
        /// </summary>
        public int Id { get; private set; }

        public FinalFuncResult OutBagEstimations { get; private set; }
        public FinalFuncResult OutBagEstimationsLogit { get; private set; }

        private alglib.logitmodel _logitModel;

        public DecisionBatch()
        {
            _batch = new List<DecisionTree>();
            Id = NCls++;
        }

        public void AddTree(DecisionTree tree)
        {
            _batch.Add(tree);
        }

        /// <summary>
        /// Количество деревьев в классификаторе
        /// </summary>
        public int CountTreesInBatch => _batch.Count;

        public void Clear()
        {
            _batch.Clear();
        }

        /// <summary>
        /// Predict probability by current batch (between 0 and 1)
        /// </summary>
        /// <param name="sarr">object coeffs to classify</param>
        /// <returns></returns>
        public double[] PredictProba(double[] sarr)
        {
            int nclasses = _batch.First().NClasses;
            double[] ret;
            if (_logitModel == null)
            {
                ret = PredictCounts(sarr);

                for (int i = 0; i < nclasses; i++)
                    ret[i] /= CountTreesInBatch;
            }
            else
            {
                var bytree = PredictByTree(sarr);
                ret = new double[nclasses];
                alglib.mnlprocess(_logitModel, bytree, ref ret);
            }

            return ret;
        }

        public double[] PredictByTree(double[] sarr)
        {
            int ntrees = _batch.Count;
            if (ntrees == 0)
                throw new InvalidOperationException("forest is empty");

            int nclasses = _batch.First().NClasses;

            var sy = new double[CountTreesInBatch];
            int k = 0;
            foreach (var tree in _batch)
            {
                if (tree.NClasses != nclasses)
                    throw new InvalidOperationException("every tree must have equal NClasses parameter");

                var probs = tree.PredictCounts(sarr);
                sy[k++] = probs[1];
            }

            return sy;
        }

        /// <summary>
        /// Count trees for each classes (between 0 and 1)
        /// </summary>
        /// <param name="sarr">object coeffs to classify</param>
        /// <returns></returns>
        public double[] PredictCounts(double[] sarr)
        {
            int ntrees = _batch.Count;
            if (ntrees == 0)
                throw new InvalidOperationException("forest is empty");

            int nclasses = _batch.First().NClasses;

            var sy = new double[nclasses];
            foreach (var tree in _batch)
            {
                if (tree.NClasses!=nclasses)
                    throw new InvalidOperationException("every tree must have equal NClasses parameter");

                var probs = tree.PredictCounts(sarr);
                for (int i = 0; i < nclasses; i++)
                    sy[i] += probs[i];
            }

            return sy;
        }

        public void Save()
        {
            string treesDir = Environment.CurrentDirectory + "\\batches";
            if (!Directory.Exists(treesDir))
                Directory.CreateDirectory(treesDir);
            var dinfo = new DirectoryInfo(treesDir);

            string fullname = dinfo.FullName + "\\" + "batch_" + $"{Id:00000.#}" + ".dmp";
            var fs = new FileStream(fullname, FileMode.Create, FileAccess.Write);

            var formatter = new BinaryFormatter();
            try
            {
                formatter.Serialize(fs, this);
            }
            catch (SerializationException e)
            {
                Logger.Log(e);
            }
            finally
            {
                fs.Close();
            }
        }

        public static DecisionBatch Load(string path)
        {
            DecisionBatch cls = null;
            FileStream fs = null;

            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                var formatter = new BinaryFormatter();
                cls = (DecisionBatch)formatter.Deserialize(fs);
                cls.Id = NCls++;
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }
            finally
            {
                fs?.Close();
            }

            return cls;
        }

        /// <summary>
        /// Creates one classifier (batch of trees)
        /// </summary>
        /// <returns></returns>
        public static DecisionBatch CreateBatch(double[,] xy, int treesInBatch, int nclasses, double rfcoeff,
            double varscoeff, int[] idx, bool parallel, bool useLogit)
        {
            var batch = new DecisionBatch();


            IEnumerable<int> source = Enumerable.Range(1, treesInBatch);
            List<DecisionTree> treeList;

            if (parallel)
                treeList = (from n in source.AsParallel()
                            select DecisionTree.CreateTree(idx, xy, nclasses, rfcoeff, varscoeff)
                        ).ToList();
            else
                treeList = (from n in source
                            select DecisionTree.CreateTree(idx, xy, nclasses, rfcoeff, varscoeff)
                        ).ToList();

            treeList.ForEach(batch.AddTree);
            CalcOutOfTheBagMetrics(treeList, xy, batch, useLogit);

            return batch;
        }

        private static void CalcOutOfTheBagMetrics(List<DecisionTree> treeList, double[,] xy, DecisionBatch batch, bool useLogit)
        {
            var rdict = new Dictionary<int, int>();
            foreach (var tree in treeList)
            {
                foreach (int id in tree.RowIndexes)
                {
                    if (!rdict.ContainsKey(id))
                        rdict.Add(id, 0);
                    rdict[id]++;
                }
            }

            int npoints = xy.GetLength(0);
            int nvars = xy.GetLength(1) - 1;
            int tnpoints = npoints - rdict.Count;

            var rlist = new RocItem[tnpoints]; // массив для оценки результата
            var ntrain = new double[tnpoints, batch.CountTreesInBatch+1]; // массив для оценки результата
            double accCoeff = rdict.Count / (double)npoints;

            for (int i=0, k=0; i < npoints; i++)
            {
                if (rdict.ContainsKey(i)) continue;

                var tobj = new double[nvars];
                for (int j = 0; j < nvars; j++)
                    tobj[j] = xy[i, j];

                rlist[k] = new RocItem();
                double[] tprob = batch.PredictProba(tobj);
                rlist[k].Prob = tprob[1];
                rlist[k].Predicted = tprob[1]>0.5?1:0;
                rlist[k].Target = Convert.ToInt32(xy[i, nvars]);

                double[] bytree = batch.PredictByTree(tobj);
                for (int j = 0; j < batch.CountTreesInBatch; j++)
                    ntrain[k, j] = bytree[j];
                ntrain[k, batch.CountTreesInBatch] = xy[i, nvars];

                k++;
            }

            Array.Sort(rlist, (o1, o2) => (1 - o1.Prob).CompareTo(1 - o2.Prob));
            batch.OutBagEstimations = ResultCalc.GetResult(rlist, 0.05);

            if (useLogit)
            {
                int info;
                alglib.mnlreport rep;
                alglib.mnltrainh(ntrain, tnpoints, batch.CountTreesInBatch, 2, out info, out batch._logitModel, out rep);

                for (int i = 0, k = 0; i < npoints; i++)
                {
                    if (rdict.ContainsKey(i)) continue;

                    var tobj = new double[nvars];
                    for (int j = 0; j < nvars; j++)
                        tobj[j] = xy[i, j];

                    rlist[k] = new RocItem();
                    double[] tprob = batch.PredictProba(tobj);
                    rlist[k].Prob = tprob[1];
                    rlist[k].Predicted = tprob[1] > 0.5 ? 1 : 0;
                    rlist[k].Target = Convert.ToInt32(xy[i, nvars]);

                    k++;
                }

                Array.Sort(rlist, (o1, o2) => (1 - o1.Prob).CompareTo(1 - o2.Prob));
                batch.OutBagEstimationsLogit = ResultCalc.GetResult(rlist, 0.05);
            }

            Logger.Log("train used: " + accCoeff + "; out_of_bag:" + batch.OutBagEstimations.AUC + "; out_of_bag_logit:" + batch.OutBagEstimationsLogit?.AUC);
        }
    }
}
