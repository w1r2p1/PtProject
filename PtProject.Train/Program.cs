﻿using PtProject.Classifier;
using PtProject.Domain.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PtProject.Train
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3 || args.Length > 6)
            {
                Logger.Log("usage: program.exe <train.csv> <test.csv> <target_name> [id1,id2,id3=, [ntrees=300 [d=0.07]]]");
                return;
            }

            string trainPath = args[0];
            string testPath = args[1];
            string target = args[2];
            string ids = args.Length >= 4 ? args[3] : ",";
            int ntrees = int.Parse(args.Length >= 5 ? args[4] : "300");
            double d = double.Parse(args.Length >= 6 ? args[5] : "0.07");

            Logger.Log("train = " + trainPath);
            Logger.Log("test = " + testPath);
            Logger.Log("target = " + target);
            Logger.Log("ids = " + ids);
            Logger.Log("ntrees = " + ntrees);
            Logger.Log("d = " + d);

            var cls = new RFClassifier(trainPath, testPath, target);

            var idsDict = ids.Split(',').ToDictionary(c => c);
            foreach (string sid in idsDict.Keys)
            {
                if (!string.IsNullOrWhiteSpace(sid))
                    cls.AddIdColumn(sid);
            }
            cls.SetRFParams(ntrees, d, 2);
            cls.LoadData();
            var result = cls.Build(true);

            Logger.Log("AUC = " + result.LastResult.AUC);
            Logger.Log("LogLoss = " + result.LastResult.LogLoss);
        }
    }
}