﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using PtProject.Domain;
using PtProject.Domain.Util;
using System.Configuration;

namespace PtProject.Loader
{
    public class DataLoader<T> : DataLoaderBase
    {
        // By default loading strategy data will be load in that list
        public List<DataRow<T>> Rows = new List<DataRow<T>>();

        // For machine-learning data will be load in that array
        public T[,] LearnRows;

        public Func<DataRow<T>, object> ProceedRowFunc;

        public override List<DataRow<object>> GetRows()
        {
            var list = new List<DataRow<object>>();
            foreach (var r in Rows)
            {
                list.Add((DataRow<object>)r);
            }
            return list;
        }

        public override Type GetItemType()
        {
            return typeof(T);
        }

        public Dictionary<int, SortedDictionary<T, int>> CntDict;
        public Dictionary<int, Dictionary<T, int>> IdxDict;
        public SortedDictionary<T, int> TargetStat = new SortedDictionary<T, int>();
        public Dictionary<T, int> ClassNumByValue = new Dictionary<T, int>();
        public Dictionary<int, T> ValueByClassNum = new Dictionary<int, T>();
        //public Dictionary<string, string> DropIds = new Dictionary<string, string>();

        private Dictionary<int, int> _idIdx = new Dictionary<int, int>(); // id indexes (many)

        /// <summary>
        /// Id column by default skipped and can be multiply
        /// </summary>
        /// <param name="col"></param>
        public void AddIdColumn(string col)
        {
            string ncol = col.ToLower();

            if (!IdName.ContainsKey(ncol)) IdName.Add(ncol.ToLower(),1);
            AddSkipColumn(ncol);
        }

        /// <summary>
        /// Target column by default skipped and can be only one
        /// </summary>
        /// <param name="col"></param>
        public void AddTargetColumn(string col)
        {
            string ncol = col.ToLower();
            TargetName = ncol;
            AddSkipColumn(ncol);
        }

        public void AddSkipColumn(string col)
        {
            string ncol = col.ToLower();

            if (string.IsNullOrWhiteSpace(ncol)) return;
            if (!SkippedColumns.ContainsKey(ncol))
                SkippedColumns.Add(ncol,1);
        }

        public void RemoveSkipColumn(string col)
        {
            string ncol = col.ToLower();

            if (string.IsNullOrWhiteSpace(ncol)) return;
            if (SkippedColumns.ContainsKey(ncol))
                SkippedColumns.Remove(ncol);
        }

        public DataLoader(string target) : this()
        {
            AddTargetColumn(target);
        }

        public DataLoader()
        {
            IdName = new Dictionary<string, int>();

            SplitSymbol = ';';
            var appSettings = ConfigurationManager.AppSettings;
            if (appSettings["SplitSymbol"] != null)
                SplitSymbol = appSettings["SplitSymbol"][0];
        }

        public DataLoader(string target, string id) : this()
        {
            AddTargetColumn(target);
            AddIdColumn(id);
        }

        public void Load(string filename)
        {
            try
            {
                if (IsLoadForLearning)
                {
                    TotalDataLines = GetDataLinesCount(filename);
                }

                var sr = new StreamReader(new FileStream(filename, FileMode.Open, FileAccess.Read), Encoding.GetEncoding(1251));
                var rnd = new Random(DateTime.Now.Millisecond + DateTime.Now.Second * 1000);

                int idx = 0;
                int nrow = 0;
                string nextline;
                int classNum = 0;
                while ((nextline = sr.ReadLine()) != null)
                {
                    idx++;
                    if (string.IsNullOrWhiteSpace(nextline)) continue;
                    string[] blocks = GetStringBlocks(nextline);

                    // header row
                    if (idx == 1)
                    {
                        for (int i = 0; i < blocks.Length; i++)
                        {
                            string cname = blocks[i]; // column name

                            if (!ColumnByIdx.ContainsKey(i))
                                ColumnByIdx.Add(i, cname);
                            if (!IdxByColumn.ContainsKey(cname))
                                IdxByColumn.Add(cname, i);
                            else
                                Logger.Log("duplicate column name: " + cname + ", exiting");
                        }

                        if (TargetName != null)
                        {
                            if (!IdxByColumn.ContainsKey(TargetName))
                            {
                                Logger.Log("data don`t have a target (" + TargetName + ") column, exiting");
                                break;
                            }
                        }

                        if (TargetName != null)
                            TargetIdx = IdxByColumn[TargetName]; // target column index

                        foreach (var iname in IdName.Keys)
                        {
                            if (!IdxByColumn.ContainsKey(iname))
                            {
                                throw new InvalidDataException("id column '" + iname + "' not found");
                            }
                            int sidx = IdxByColumn[iname];
                            if (!_idIdx.ContainsKey(sidx)) _idIdx.Add(sidx, 1);
                        }

                        var toDel = (from t in SkippedColumns.Keys where !IdxByColumn.ContainsKey(t) select t).ToList();
                        toDel.ForEach(c => SkippedColumns.Remove(c));

                        NVars = ColumnByIdx.Count - SkippedColumns.Count;

                        continue;
                    }

                    // data row
                    nrow++;

                    if (blocks.Length > IdxByColumn.Count)
                    {
                        Logger.Log("error parsing row #" + nrow);
                        continue;
                    }

                    if (rnd.NextDouble() >= LoadFactor) continue;

                    var row = new DataRow<T>();

                    // target 
                    if (TargetName != null) row.Target = ParseValue(blocks[TargetIdx]);

                    // creating composite id
                    row.Id = GetStringId(blocks);
                    if (string.IsNullOrEmpty(row.Id)) row.Id = nrow.ToString();

                    // drop columns
                    //if (DropIds != null && DropIds.ContainsKey(row.Id)) continue;

                    // save stats for target value
                    if (!TargetStat.ContainsKey(row.Target))
                        TargetStat.Add(row.Target, 0);
                    TargetStat[row.Target]++;   // count by target

                    if (!ClassNumByValue.ContainsKey(row.Target))
                        ClassNumByValue.Add(row.Target, classNum++); // class by target

                    if (!ValueByClassNum.ContainsKey(ClassNumByValue[row.Target]))
                        ValueByClassNum.Add(ClassNumByValue[row.Target], row.Target); // class by target

                    // --------------------------- loading for learning -------------------------------
                    if (IsLoadForLearning)
                    {
                        if (LearnRows == null)
                        {
                            LearnRows = new T[TotalDataLines, NVars + 1]; // all variables +1 for target
                        }

                        for (int i = 0, k = 0; i < blocks.Length; i++)
                        {
                            string cval = blocks[i];
                            string colname = ColumnByIdx[i];
                            if (SkippedColumns.ContainsKey(colname))
                                continue;

                            LearnRows[nrow - 1, k++] = ParseValue(cval);
                        }
                        LearnRows[nrow - 1, NVars] = row.Target;
                    }
                    else 
                    {
                        // --------------------------- loading for analyse -----------------------------------
                        var carray = new T[NVars];

                        for (int i = 0, k = 0; i < blocks.Length; i++)
                        {
                            string cval = blocks[i];
                            if (ColumnByIdx.ContainsKey(i))
                            {
                                string colname = ColumnByIdx[i];
                                if (SkippedColumns.ContainsKey(colname))
                                    continue;

                                if (!ColumnByIdxRow.ContainsKey(k))
                                    ColumnByIdxRow.Add(k, colname);

                                carray[k] = ParseValue(cval);
                                k++;
                            }
                            else
                            {
                                Logger.Log("error parsing id=" + row.Id);
                            }
                        }

                        row.Coeffs = carray;
                        if (ProceedRowFunc == null)
                            Rows.Add(row);
                        else
                            ProceedRowFunc(row);
                    }

                    if (idx % 12345 == 0) Logger.Log(idx + " lines loaded");
                    if (MaxRowsLoaded != 0 && idx > MaxRowsLoaded) break;
                }

                Logger.Log((idx-1) + " lines loaded;");
            }
            catch (Exception e)
            {
                Logger.Log(e);
                throw e;
            }
        }

        private string[] GetStringBlocks(string nextline)
        {
            string[] blocks;
            if (SplitSymbol != ',')
                blocks = nextline.ToLower().Replace(',', '.').Split(SplitSymbol);
            else
                blocks = nextline.ToLower().Split(SplitSymbol);
            if (blocks != null)
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] == null) continue;
                    blocks[i] = blocks[i].Trim('"');
                }
            }

            return blocks;
        }

        private string GetStringId(string[] blocks)
        {
            var sb = new StringBuilder();
            int nidx = 0;
            foreach (var sidx in _idIdx.Keys)
            {
                if (nidx == 0)
                    sb.Append(blocks[sidx]);
                else
                    sb.Append(";" + blocks[sidx]);
                nidx++;
            }
            return sb.ToString();
        }

        private int GetDataLinesCount(string filename)
        {
            int result = 0;
            try
            {
                var sr = new StreamReader(new FileStream(filename, FileMode.Open, FileAccess.Read), Encoding.GetEncoding(1251));

                string nextline = null;
                int idx = 0;
                while ((nextline = sr.ReadLine()) != null)
                {
                    idx++;
                    if (idx == 1) continue; // skip header

                    if (!string.IsNullOrWhiteSpace(nextline))
                        result++;
                }
            }
            catch (Exception e)
            {
                Logger.Log(e);
            }

            return result;
        }

        private T ParseValue(string str)
        {
            double val;
            bool isok = double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out val);
            T fval = default(T);
            if (!isok)
            {
                if (!StringValues.ContainsKey(str))
                    StringValues.Add(str, StringValues.Count);

                val = StringValues[str];
            }
            fval = (T)Convert.ChangeType(val, typeof(T));
            return fval;
        }
    }
}
