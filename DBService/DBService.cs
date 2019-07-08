﻿using System;

using Kernel.Interface;
using Kernel.Common;
using Kernel.QueueManager;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObjectManager;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using DBService.DBContext;



namespace DBService
{
    public class DBService : IService, IDisposable
    {

        private string _SeviceName = "DBService";
        public string ServiceName
        {
            get
            {
                return this._SeviceName;
            }
        }

        // For IOC/DI Used
        private readonly IQueueManager _QueueManager;
        private readonly IManagement _ObjectManager;
        private ObjectManager.ObjectManager _objectmanager = null;
        private readonly ILogger<DBService> _logger;

        // ---- Base DB Partaker 
        public ConcurrentDictionary<string, DBPartaker> _BaseDBPartaker = null;

        //------ Key = SerialID_Gateway_Device   Value DeviceObject
        public ConcurrentDictionary<string, DBContext.IOT_DEVICE> _IOT_Device = null;

        //------ Key = SerialID_Gateway_Device    value <itemName, index>
        public  ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _EDC_Label_Data = null;
        public  ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _Sync_EDC_Label_Data = null;

        private static Timer timer_refresh_database;
        private static Timer timer_report_DB;

        //----- Key = DB Provider_ConnectString ------
        private ConcurrentDictionary<string, List<DBPartaker>> _dic_DB_Partaker =  null;

        // -- Delegate Method
        public delegate List<string> Get_EDC_Label_Data_Event(string SerialID, string GatewayID, string DeviceID);
        public delegate void Update_EDC_Label_Event(string _Serial_ID, string _GateWayID, string _DeviceID, List<string> UpdateTagInfo);
        public delegate void Add_DBPartaker_to_dict_Event( DBPartaker DBP);
       
        public DBService(ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
        {
            _QueueManager = serviceProvider.GetService<IQueueManager>();
            _ObjectManager = serviceProvider.GetServices<IManagement>().Where(o => o.ManageName.Equals("ObjectManager")).FirstOrDefault();
            _logger = loggerFactory.CreateLogger<DBService>();
        }

        public void Init()
        {
            try
            {
                _IOT_Device          = new ConcurrentDictionary<string, DBContext.IOT_DEVICE>();
                _EDC_Label_Data      = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
                _Sync_EDC_Label_Data = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
                _dic_DB_Partaker     = new ConcurrentDictionary<string, List<DBPartaker>>();
                _BaseDBPartaker      = new ConcurrentDictionary<string, DBPartaker>();

                int DB_Refresh_Interval = 10000;  // 10 秒
                int DB_Report_Interval = 6000;    // 6 秒
                Timer_Refresh_DB(DB_Refresh_Interval);
                Timer_Report_DB(DB_Report_Interval);

            }

            catch (Exception ex)
            {
                Dispose();
                 _logger.LogError("DB Service Initial Faild, Exception Msg = " + ex.Message);
            }
         }

        public void Dispose()
        {
            
        }

        #region Timer Thread
        public void Timer_Refresh_DB(int interval)
        {
            if (interval == 0)
                interval = 10000;  

            //使用匿名方法，建立帶有參數的委派
            System.Threading.Thread Thread_Timer_Refresh_DB = new System.Threading.Thread
            (
               delegate (object value)
               {
                   int Interval = Convert.ToInt32(value);
                   timer_refresh_database = new Timer(new TimerCallback(Builde_Update_DB_Information), null, 0, Interval);
               }
            );
            Thread_Timer_Refresh_DB.Start(interval);
        }

        public void Timer_Report_DB(int interval)
        {
            if (interval == 0)
                interval = 10000;  
            //使用匿名方法，建立帶有參數的委派
            System.Threading.Thread Thread_Timer_Report_DB = new System.Threading.Thread
            (
               delegate (object value)
               {
                   int Interval = Convert.ToInt32(value);
                   timer_report_DB = new Timer(new TimerCallback(DB_TimerTask), null, 1000, Interval);
               }
            );
            Thread_Timer_Report_DB.Start(interval);
        }
        #endregion

        #region Time Thread Method 
        public void DB_TimerTask(object timerState)
        {
            try
            {
                foreach( KeyValuePair<string, List<DBPartaker>> ele in _dic_DB_Partaker)
                {
                    List<DBPartaker> _DBProcess;
                   
                    _dic_DB_Partaker.TryRemove(ele.Key, out _DBProcess);
                    string[] temp = ele.Key.Split('_');
                    string Provider = temp[1].ToString();
                    string ConnectionStr = temp[2].ToString();

                    using (var db = new DBContext.IOT_DbContext(Provider, ConnectionStr))
                    {
                        foreach(DBPartaker DBP in _DBProcess)
                        {

                            DBContext.IOT_DEVICE Device = null;
                            ConcurrentDictionary<string, int> Dict_EDC_Label = null;

                            string key = string.Concat(DBP.serial_id, "_", DBP.gateway_id, "_", DBP.device_id);
                            _IOT_Device.TryGetValue(key, out Device);
                            _EDC_Label_Data.TryGetValue(key, out Dict_EDC_Label);

                            DBContext.IOT_DEVICE_EDC oIoT_DeviceEDC = new DBContext.IOT_DEVICE_EDC();

                            oIoT_DeviceEDC.device_id = DBP.device_id;

                            string InsertDBInfo = string.Empty;

                            foreach (Tuple<string,string> items in  DBP.Report_Item)
                            {
                                int ReportIndex = 0;
                                Dict_EDC_Label.TryGetValue(items.Item1, out ReportIndex);
                                if( ReportIndex ==0 )
                                {
                                    continue;
                                }
                                else
                                {
                                    string ReportValue = (IsNumeric(items.Item2) == true) ? items.Item2 : "999999";
                                    string ReportPropertyName = string.Concat("data_value_", ReportIndex.ToString("00"));
                                    oIoT_DeviceEDC.SetPropertyValue(ReportPropertyName, ReportValue);
                                    InsertDBInfo = string.Concat(InsertDBInfo, "_", string.Format("ItemName:{0}, ItemValue:{1}, ItemPosi:{2}.", items.Item1, items.Item2, ReportIndex.ToString("00")));
                                }
                            }

                            _logger.LogInformation(string.Format("DB Service Insert DB : {0}, Device : {1}. ", ele.Key, key));
                            _logger.LogTrace(string.Format("DB Insert Trace {0}. ", InsertDBInfo));

                            oIoT_DeviceEDC.clm_date_time = DateTime.Now;
                            oIoT_DeviceEDC.clm_user = "SYSADM";
                            oIoT_DeviceEDC.AddDB(db, Device, oIoT_DeviceEDC);

                        }
                        _DBProcess.Clear();
                        db.SaveChanges();
                    }
                       
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(string.Format("Insert_DB Faild ex :{0}. ", ex.Message));
            }
        }
        public void Builde_Update_DB_Information(object timerState)
        {
            try
            {
                // foreach (cls_DB_Info DB_info in _objectmanager.DBManager.dbconfig_list)

                foreach (KeyValuePair<string, DBPartaker> BaseDBP in this._BaseDBPartaker )
                {
                    _logger.LogInformation(string.Format("Update_DB_Info IOT_DEVICE and EDC_LABEL Info, SerialNo = {0}, GatewayID = {1}, DeviceID = {2}", BaseDBP.Value.serial_id, BaseDBP.Value.gateway_id, BaseDBP.Value.device_id));

                    // "MS SQL" / "My SQL"  ""MS SQL"", "server= localhost;database=IoTDB;user=root;password=qQ123456")
                    using (var db = new DBContext.IOT_DbContext(BaseDBP.Value.db_type, BaseDBP.Value.connection_string))
                    {
                        var vIOT_Device_Result = db.IOT_DEVICE.AsQueryable();
                        var _IOT_Status = vIOT_Device_Result.ToList();

                        foreach (DBContext.IOT_DEVICE Device in _IOT_Status)
                        {
                            string _IOT_Device_key = string.Concat(BaseDBP.Value.serial_id, "_", Device.gateway_id, "_", Device.device_id);
                            DBContext.IOT_DEVICE _IOT_Device_Value = (DBContext.IOT_DEVICE)Device.Clone();
                            _IOT_Device.AddOrUpdate(_IOT_Device_key, _IOT_Device_Value, (key, oldvalue) => _IOT_Device_Value);
                        }

                        var vEDC_Label_Result = db.IOT_DEVICE_EDC_LABEL.AsQueryable().ToList();
                        foreach (DBContext.IOT_DEVICE_EDC_LABEL _EDC_Label_key in vEDC_Label_Result)
                        {
                            // Device ID  is key  use DB裡面的變數
                            string _IOT_Device_key = string.Concat(BaseDBP.Value.serial_id, "_", BaseDBP.Value.gateway_id, "_", _EDC_Label_key.device_id);
                            ConcurrentDictionary<string, int> _Sub_EDC_Labels = this._EDC_Label_Data.GetOrAdd(_EDC_Label_key.device_id, new ConcurrentDictionary<string, int>());
                            _Sub_EDC_Labels.AddOrUpdate(_EDC_Label_key.data_name, _EDC_Label_key.data_index, (key, oldvalue) => _EDC_Label_key.data_index);
                            this._EDC_Label_Data.AddOrUpdate(_EDC_Label_key.device_id, _Sub_EDC_Labels, (key, oldvalue) => _Sub_EDC_Labels);
                        }
                    }
                }


                if (_Sync_EDC_Label_Data.Count != 0)
                {
                    foreach (KeyValuePair<string, ConcurrentDictionary<string, int>> kvp in _Sync_EDC_Label_Data)
                    {
                        ConcurrentDictionary<string, int> _Process;
                        string Key = kvp.Key;
                        _Sync_EDC_Label_Data.TryRemove(Key, out _Process);

                        _logger.LogInformation(string.Format("Upload_DB_Info EDC_LABEL Info, (SerialNo,GatewayID,DeviceID ) = ({0})", Key));

                        string[] temp = Key.Split('_');
                        string SerialID = temp[1].ToString();
                        string GatewayID = temp[2].ToString();
                        string DeviceID = temp[3].ToString();


                        DBPartaker DB_info = null;
                        this._BaseDBPartaker.TryGetValue(SerialID, out DB_info);
                       // cls_DB_Info DB_info = _objectmanager.DBManager.dbconfig_list.Where(p => p.serial_id == SerialID).FirstOrDefault();

                        if (DB_info == null)
                        {
                            _logger.LogWarning(string.Format("Upload_DB_Info Faild DB Serial {0} not Exist in DBManager)", SerialID));
                        }
                        else
                        {

                            using (var db = new DBContext.IOT_DbContext(DB_info.db_type, DB_info.connection_string))
                            {
                                string InsertEDCLabelInfo = string.Empty;

                                foreach (KeyValuePair<string, int> _EDC_item in _Process)
                                {
                                    DBContext.IOT_DEVICE_EDC_LABEL oIoT_Device_EDC_label = new DBContext.IOT_DEVICE_EDC_LABEL();
                                    oIoT_Device_EDC_label.device_id = DB_info.device_id;
                                    oIoT_Device_EDC_label.data_name = _EDC_item.Key;
                                    oIoT_Device_EDC_label.data_index = _EDC_item.Value;
                                    oIoT_Device_EDC_label.clm_date_time = DateTime.Now;
                                    oIoT_Device_EDC_label.clm_user = "system";
                                    db.Add(oIoT_Device_EDC_label);
                                    InsertEDCLabelInfo = string.Concat(InsertEDCLabelInfo, "_", string.Format("ItemName:{0}, ItemPosi:{1}.", _EDC_item.Key, _EDC_item.Value));

                                }
                                db.SaveChanges();
                                _logger.LogTrace(string.Format("DB Insert EDC Label Trace {0}. ", InsertEDCLabelInfo));
                            }

                           
                        }
                    }
                }
            }

            catch (Exception ex)
            { 
               
            }

        }
        #endregion

        #region Delegate Method
        public void Add_DBPartaker_to_dict( DBPartaker DBP )
        {
            string Key = string.Concat(DBP.db_type, "_", DBP.connection_string);
            List<DBPartaker> _Current = this._dic_DB_Partaker.GetOrAdd(Key, new List<DBPartaker>());
            _Current.Add(DBP);
            this._dic_DB_Partaker.AddOrUpdate(Key, _Current, (key, oldvalue) => _Current);

            //------ Update Base DB Info ------

            DBPartaker _CurrentDBPartaker = this._BaseDBPartaker.GetOrAdd(Key, new DBPartaker());
            _CurrentDBPartaker = (DBPartaker)DBP.Clone();
            _CurrentDBPartaker.Report_Item.Clear();
            this._BaseDBPartaker.AddOrUpdate(Key, _CurrentDBPartaker, (key, oldvalue) => _CurrentDBPartaker);

        }

        public List<string > Get_EDC_Label_Data (string _Serial_ID, string _GateWayID, string _DeviceID)
        {
            string key = string.Concat(_Serial_ID, "_", _GateWayID, "_", _DeviceID);
            ConcurrentDictionary<string, int> _Sub_EDC_Labels = _EDC_Label_Data[key];
            return _Sub_EDC_Labels.Keys.ToList();
        }

        public void Update_EDC_Label_Data (string _Serial_ID, string _GateWayID, string _DeviceID, List<string> UpdateTagInfo)
        {
            string _IOT_Device_key = string.Concat(_Serial_ID, "_", _GateWayID, "_", _DeviceID);
            ConcurrentDictionary<string, int> _Current_Device_EDC_Label = this._EDC_Label_Data.GetOrAdd(_IOT_Device_key, new ConcurrentDictionary<string, int>());
            ConcurrentDictionary<string, int> _Sync_Device_EDC_Label    = this._Sync_EDC_Label_Data.GetOrAdd(_IOT_Device_key, new ConcurrentDictionary<string, int>());
            List<int> _lstIndex = _Current_Device_EDC_Label.Select(t => t.Value).ToList();
            int Index = _lstIndex.Max();
            foreach (string New_EDC_items in UpdateTagInfo)
            {
               Index ++;
               _Current_Device_EDC_Label.AddOrUpdate(New_EDC_items, Index, (key, oldvalue) => Index);
               _Sync_Device_EDC_Label.AddOrUpdate(New_EDC_items, Index, (key, oldvalue) => Index);
            }
            this._EDC_Label_Data.AddOrUpdate(_IOT_Device_key, _Current_Device_EDC_Label, (key, oldvalue) => _Current_Device_EDC_Label);
            this._Sync_EDC_Label_Data.AddOrUpdate(_IOT_Device_key, _Sync_Device_EDC_Label, (key, oldvalue) => _Sync_Device_EDC_Label);
        }

        #endregion


        public void ReceiveMQTTData(xmlMessage InputData)
        {
            // Parse Mqtt Topic
            string Topic = InputData.MQTTTopic;
            string Payload = InputData.MQTTPayload;
            try
            {
                ProcDBData DBProc = new ProcDBData(Payload, Get_EDC_Label_Data, Update_EDC_Label_Data, Add_DBPartaker_to_dict);
                if (DBProc != null)
                {
                    ThreadPool.QueueUserWorkItem(o => DBProc.ThreadPool_Proc());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(string.Format("DB Service Handle ReceiveMQTTData Exception Msg : {0}. ", ex.Message));
            }

        }

        #region MISC
        public bool IsNumeric(object Expression)
        {
           
            bool isNum;
           
            double retNum;

            isNum = Double.TryParse(Convert.ToString(Expression), System.Globalization.NumberStyles.Any, System.Globalization.NumberFormatInfo.InvariantInfo, out retNum);

            return isNum;
        }

        #endregion

    }

    public class ProcDBData
    {
        DBPartaker objDB = null;
        public DBService.Get_EDC_Label_Data_Event _GetEDCLabel;
        public DBService.Update_EDC_Label_Event _UpdateEDCLabel;
        public DBService.Add_DBPartaker_to_dict_Event _Add_DBPartakertoDict;
        public ProcDBData(string inputdata, DBService.Get_EDC_Label_Data_Event GetEDCLabel, DBService.Update_EDC_Label_Event UpdateEDCLabel, DBService.Add_DBPartaker_to_dict_Event Add_DBPartakertoDict)
        {
            try
            {
                this.objDB = JsonConvert.DeserializeObject<DBPartaker>(inputdata.ToString());
                _GetEDCLabel = GetEDCLabel;
                _UpdateEDCLabel = UpdateEDCLabel;
                _Add_DBPartakertoDict = Add_DBPartakertoDict;
            }
            catch
            {
                this.objDB = null;
            }

        }
        public void ThreadPool_Proc()
        {
            List<string> EDCLabel = _GetEDCLabel(objDB.serial_id, objDB.gateway_id, objDB.device_id);
            List<string> ReporLabel = this.objDB.Report_Item.Select(t => t.Item1).ToList();
            List<string> Diff = ReporLabel.Except(EDCLabel).ToList();

            if(Diff.Count > 0)
            {
                _UpdateEDCLabel(this.objDB.serial_id, this.objDB.gateway_id, this.objDB.device_id, Diff);
            }

            _Add_DBPartakertoDict(this.objDB);

        }
    }
}
