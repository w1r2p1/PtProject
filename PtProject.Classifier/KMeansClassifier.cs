﻿using PtProject.Domain.Util;
using PtProject.Loader;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using FType = System.Double;
using System.Collections.Concurrent;
using System.Text;

namespace PtProject.Classifier
{
    public class KMeansClassifier : AbstractClassifier
    {
        private DataLoader<FType> _trainLoader;

        public int KNeighbors = 2;
        public double Pow = 2;
        public int NeighborsOffset = 0;

        private int _nclasses = 2;
        private double[] _avgs;
        private double[] _vars;
        private double[] _corrs;

        public KMeansClassifier(/*Dictionary<string,object> prms=null*/) : base(/*prms*/)
        {
            LoadDefaultParams();
        }

        /// <summary>
        /// Default parameters for random-forest algorithm
        /// </summary>
        public void LoadDefaultParams()
        {
            string kn = ConfigReader.Read("KNeighbors");
            if (kn != null) KNeighbors = int.Parse(kn);
            _prms.Add("KNeighbors", KNeighbors);

            string pw = ConfigReader.Read("Pow");
            if (pw != null) Pow = double.Parse(pw.Replace(',', '.'), CultureInfo.InvariantCulture);
            _prms.Add("Pow", Pow);

            string nofs = ConfigReader.Read("NeighborsOffset");
            if (nofs != null) NeighborsOffset = int.Parse(nofs);
            _prms.Add("NeighborsOffset", NeighborsOffset);
        }

        public override ClassifierResult Build()
        {
            _avgs = new double[_trainLoader.NVars];
            _vars = new double[_trainLoader.NVars];
            _corrs = new double[_trainLoader.NVars];

            double tavg = 0;
            double t1sq = 0;
            double t2sq = 0;

            // sum per column
            foreach (var row in _trainLoader.Rows)
            {
                for (int i = 0; i < row.Coeffs.Length; i++)
                    _avgs[i] += row.Coeffs[i];
                tavg += row.Target;
            }

            // average per column
            for (int i=0;i<_avgs.Length;i++)
            {
                _avgs[i] /= _trainLoader.TotalDataLines;
            }
            tavg /= _trainLoader.TotalDataLines;

            // variance per column
            foreach (var row in _trainLoader.Rows)
            {
                double t2 = row.Target - tavg;

                for (int i = 0; i < row.Coeffs.Length; i++)
                {
                    _vars[i] += Math.Pow(row.Coeffs[i] - _avgs[i], 2);
                    double t1 = row.Coeffs[i] - _avgs[i];
                    _corrs[i] += t1 * t2;
                    t1sq += Math.Pow(t1, 2);
                    t2sq += Math.Pow(t2, 2);
                }
            }
            for (int i = 0; i < _avgs.Length; i++)
            {
                _vars[i] /= (_trainLoader.TotalDataLines-1);
                _corrs[i] /= Math.Sqrt(t1sq) * Math.Sqrt(t2sq);
                _corrs[i] = Math.Abs(_corrs[i]);
                if (double.IsNaN(_vars[i])) _vars[i] = 0;
                if (double.IsNaN(_corrs[i])) _corrs[i] = 0;
            }

            // modifying data
            foreach (var row in _trainLoader.Rows)
            {
                for (int i = 0; i < row.Coeffs.Length; i++)
                {
                    row.Coeffs[i] -= _avgs[i];
                    row.Coeffs[i] /= _vars[i]>0?Math.Sqrt(_vars[i]):1;
                }
            }

            return null;
        }

        public override int LoadClassifier()
        {
            LoadData();
            Build();

            return 0;
        }

        public override void LoadData()
        {
            _trainLoader = TargetName != null ? new DataLoader<FType>(TargetName) : new DataLoader<FType>();

            if (!File.Exists(TrainPath))
            {
                Logger.Log("train file " + TrainPath + " not found");
                throw new FileNotFoundException("", TrainPath);
            }

            // loading train file
            _trainLoader.AddIdsString(IdName);
            _trainLoader.Load(TrainPath);
        }

        public override ObjectClassificationResult PredictProba(double[] sarr)
        {
            var result = new ObjectClassificationResult();
            // modifying data
            for (int i = 0; i < sarr.Length; i++)
            {
                sarr[i] -= _avgs[i];
                sarr[i] /= _vars[i] > 0 ? Math.Sqrt(_vars[i]) : 1;
            }

            var source = _trainLoader.Rows;
            var tlist = new ConcurrentBag<Tuple<double, double>>();
            var taskList = new List<Tuple<double, double>>();

            if (IsParallel)
                taskList = (from row in source.AsParallel()
                            select ProceedRow(sarr, row)
                        ).ToList();
            else
                taskList = (from row in source
                         select ProceedRow(sarr, row)
                        ).ToList();

            taskList.ForEach(t => tlist.Add(t));

            var slist = tlist.OrderBy(t => t.Item1).ToArray();
            var pcounts = new SortedDictionary<double, double>();

            //var sb = new StringBuilder();

            for (int i= NeighborsOffset; i<KNeighbors + NeighborsOffset; i++)
            {
                var targ = slist[i].Item2;
                if (!pcounts.ContainsKey(targ))
                    pcounts.Add(targ, 0);
                pcounts[targ]++;

                //sb.Append(slist[i].Item1.ToString("F08")+';');
            }

            //result.ObjectInfo = sb.ToString();
            result.Probs = new double[_nclasses];
            for (int i=0;i<_nclasses;i++)
            {
                result.Probs[i] = pcounts.ContainsKey(i) ? pcounts[i] / KNeighbors : 0;
            }

            return result;
        }

        private Tuple<double, double> ProceedRow(double[] sarr, Domain.DataRow<double> row)
        {
            double rsum = 0;
            for (int i = 0; i < row.Coeffs.Length; i++)
            {
                double diff = (sarr[i] - row.Coeffs[i]);// * _corrs[i];
                rsum += Math.Pow(diff, Pow);
            }
            double dist = Math.Pow(rsum, 1 / Pow);
            var tp = new Tuple<double, double>(dist, row.Target);
            return tp;
        }
    }
}
