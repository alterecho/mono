//
// Mono.Data.TdsClient.Internal.Tds.cs
//
// Author:
//   Tim Coleman (tim@timcoleman.com)
//
// Copyright (C) 2002 Tim Coleman
//

using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Mono.Data.TdsClient.Internal {
        internal abstract class Tds : Component, ITds
	{
		#region Fields

		TdsComm comm;
		TdsVersion tdsVersion;

		int packetSize;
		string dataSource;
		string database;
		string databaseProductName;
		string databaseProductVersion;
		int databaseMajorVersion;

		string charset;
		string language;

		bool connected = false;
		bool moreResults;

		Encoding encoder;
		TdsServerType serverType;
		bool autoCommit;

		bool doneProc;
		TdsPacketRowResult currentRow = null;
		TdsPacketColumnNamesResult columnNames;
		TdsPacketColumnInfoResult columnInfo;
		TdsPacketTableNameResult tableNames;

		bool queryInProgress;
		int cancelsRequested;
		int cancelsProcessed;

		bool isDone;
		bool isDoneInProc;

		ArrayList outputParameters = new ArrayList ();
		TdsInternalErrorCollection messages = new TdsInternalErrorCollection ();

		int recordsAffected = 0;

		#endregion // Fields

		#region Properties

		protected string Charset {
			get { return charset; }
		}

		public bool DoneProc {
			get { return doneProc; }
		}

		protected string Language {
			get { return language; }
		}

		protected TdsPacketColumnNamesResult ColumnNames {
			get { return columnNames; }
		}

		public TdsPacketRowResult ColumnValues {
			get { return currentRow; }
		}

		protected TdsComm Comm {
			get { return comm; }
		}

		public string Database {
			get { return database; }
		}

		public string DataSource {
			get { return dataSource; }
		}

		public bool IsConnected {
			get { return connected; }
			set { connected = value; }
		}

		public bool MoreResults {
			get { return moreResults; }
		}

		public int PacketSize {
			get { return packetSize; }
		}

		public int RecordsAffected {
			get { return recordsAffected; }
			set { recordsAffected = value; }
		}

		public string ServerVersion {
			get { return databaseProductVersion; }
		}

		public TdsPacketColumnInfoResult Schema {
			get { return columnInfo; }
		}

		public TdsVersion TdsVersion {
			get { return tdsVersion; }
		}

		public ArrayList OutputParameters {
			get { return outputParameters; }
			set { outputParameters = value; }
		}

		#endregion // Properties

		#region Events

		public event TdsInternalErrorMessageEventHandler TdsErrorMessage;
		public event TdsInternalInfoMessageEventHandler TdsInfoMessage;

		#endregion // Events

		#region Constructors

		public Tds (string dataSource, int port, int packetSize, int timeout, TdsVersion tdsVersion)
		{
			this.tdsVersion = tdsVersion;
			this.packetSize = packetSize;
			this.dataSource = dataSource;

			comm = new TdsComm (dataSource, port, packetSize, timeout, tdsVersion);
		}

		#endregion // Constructors

		#region Public Methods

		public void Cancel ()
		{
			if (queryInProgress) {
				if (cancelsRequested == cancelsProcessed) {
					comm.StartPacket (TdsPacketType.Cancel);
					comm.SendPacket ();
					cancelsRequested += 1;
				}
			}	
		}
	
		public abstract bool Connect (TdsConnectionParameters connectionParameters);

		public static TdsTimeoutException CreateTimeoutException (string dataSource, string method)
		{
			string message = "Timeout expired. The timeout period elapsed prior to completion of the operation or the server is not responding.";
			return new TdsTimeoutException (0, 0, message, -2, method, dataSource, "Mono TdsClient Data Provider", 0);
		}

		public void Disconnect ()
		{
			TdsPacketResult result = null;

			comm.StartPacket (TdsPacketType.Logoff);
			comm.Append ((byte) 0);
			comm.SendPacket ();	

			bool done = false;
			do {
				result = ProcessSubPacket ();
				if (result != null) {
					switch (result.GetType ().ToString ()) {
					case "Mono.Data.TdsClient.Internal.TdsPacketEndTokenResult" :
						done = !((TdsPacketEndTokenResult) result).MoreResults;
						break;
					}
				}
			} while (!done);
		}

		public int ExecuteNonQuery (string sql)
		{
			return ExecuteNonQuery (sql, 0);
		}

		public int ExecuteNonQuery (string sql, int timeout)
		{
			TdsPacketResult result = null;
			messages.Clear ();
			doneProc = false;

			if (sql.Length > 0) {
				comm.StartPacket (TdsPacketType.Query);
				comm.Append (sql);
				comm.SendPacket ();
			}

			CheckForData (timeout);

			bool done = false;
			while (!done) {
				result = ProcessSubPacket ();

				if (result != null) {
					switch (result.GetType ().ToString ()) {
					case "Mono.Data.TdsClient.Internal.TdsPacketColumnNamesResult" :
						columnNames = (TdsPacketColumnNamesResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketColumnInfoResult" :
						columnInfo = (TdsPacketColumnInfoResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketRowResult" :
						currentRow = (TdsPacketRowResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketTableNameResult" :
						tableNames = (TdsPacketTableNameResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketEndTokenResult" :
						done = !((TdsPacketEndTokenResult) result).MoreResults;
						break;
					}
				}
			}
			if (sql.Trim ().ToUpper ().StartsWith ("SELECT"))
				recordsAffected = -1;
			else
				recordsAffected = ((TdsPacketEndTokenResult) result).RowCount;
			return recordsAffected;
			
		}

		public void ExecuteQuery (string sql)
		{
			ExecuteQuery (sql, 0);
		}

		public void ExecuteQuery (string sql, int timeout)
		{
			moreResults = true;
			doneProc = false;
			outputParameters.Clear ();

			if (sql.Length > 0) {
				comm.StartPacket (TdsPacketType.Query);
				comm.Append (sql);
				comm.SendPacket ();
			}

			CheckForData (timeout);
		}

		public bool NextResult ()
		{
			if (!moreResults)
				return false;
			TdsPacketResult result = null;

			bool done = false;
			while (!done) {
				result = ProcessSubPacket ();

				if (result != null) {
					switch (result.GetType ().ToString ()) {
					case "Mono.Data.TdsClient.Internal.TdsPacketColumnNamesResult" :
						columnNames = (TdsPacketColumnNamesResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketColumnInfoResult" :
						columnInfo = (TdsPacketColumnInfoResult) result;
						if (comm.Peek () != (byte) TdsPacketSubType.TableName) {
							return true;
						}
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketRowResult" :
						currentRow = (TdsPacketRowResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketTableNameResult" :
						tableNames = (TdsPacketTableNameResult) result;
						break;
					case "Mono.Data.TdsClient.Internal.TdsPacketEndTokenResult" :
						done = !((TdsPacketEndTokenResult) result).MoreResults;
						break;
					}
				}
			}

			return false;
		}

		public bool NextRow ()
		{
			TdsPacketResult result = null;
			bool done = false;
			do {
				result = ProcessSubPacket ();
				if (result != null) {
					switch (result.GetType ().ToString ()) {
					case "Mono.Data.TdsClient.Internal.TdsPacketRowResult" :
						currentRow = (TdsPacketRowResult) result;
						return true;
					case "Mono.Data.TdsClient.Internal.TdsPacketEndTokenResult" :
						return false;
					}
				}
			} while (!done);

			return false;
		}

		public void SkipToEnd ()
		{
			while (moreResults)
				NextResult ();
		}

		#endregion // Public Methods

		#region // Private Methods

		[MonoTODO ("Is cancel enough, or do we need to drop the connection?")]
		private void CheckForData (int timeout) 
		{
			if (timeout > 0 && !comm.Poll (timeout, SelectMode.SelectRead)) {
				Cancel ();
				throw CreateTimeoutException (dataSource, "CheckForData()");
			}
		}
	
		protected TdsInternalInfoMessageEventArgs CreateTdsInfoMessageEvent (TdsInternalErrorCollection errors)
		{
			return new TdsInternalInfoMessageEventArgs (errors);
		}

		protected TdsInternalErrorMessageEventArgs CreateTdsErrorMessageEvent (byte theClass, int lineNumber, string message, int number, string procedure, string server, string source, byte state)
		{
			return new TdsInternalErrorMessageEventArgs (new TdsInternalError (theClass, lineNumber, message, number, procedure, server, source, state));
		}

		private void FinishQuery (bool wasCancelled, bool moreResults)
		{
			if (!moreResults) 
				queryInProgress = false;
			if (wasCancelled)
				cancelsProcessed += 1;
			if (messages.Count > 0 && !moreResults) 
				OnTdsInfoMessage (CreateTdsInfoMessageEvent (messages));
		}

		private object GetColumnValue (TdsColumnType colType, bool outParam)
		{
			return GetColumnValue (colType, outParam, -1);
		}

		private object GetColumnValue (TdsColumnType colType, bool outParam, int ordinal)
		{
			int len;
			object element = null;

			switch (colType) {
			case TdsColumnType.IntN :
				if (outParam)
					comm.Skip (1);
				element = GetIntValue (colType);
				break;
			case TdsColumnType.Int1 :
			case TdsColumnType.Int2 :
			case TdsColumnType.Int4 :
				element = GetIntValue (colType);
				break;
			case TdsColumnType.Image :
				if (outParam) 
					comm.Skip (1);
				element = GetImageValue ();
				break;
			case TdsColumnType.Text :
				if (outParam) 
					comm.Skip (1);
				element = GetTextValue (false);
				break;
			case TdsColumnType.NText :
				if (outParam) 
					comm.Skip (1);
				element = GetTextValue (true);
				break;
			case TdsColumnType.Char :
			case TdsColumnType.VarChar :
				if (outParam)
					comm.Skip (1);
				element = GetStringValue (false, false);
				break;
			case TdsColumnType.BigVarBinary :
				comm.GetTdsShort ();
				len = comm.GetTdsShort ();
				element = comm.GetBytes (len, true);
				break;
			case TdsColumnType.BigVarChar :
				comm.Skip (2);
				element = GetStringValue (false, false);
				break;
			case TdsColumnType.NChar :
			case TdsColumnType.NVarChar :
				if (outParam) 
					comm.Skip (1);
				element = GetStringValue (true, false);
				break;
			case TdsColumnType.Real :
			case TdsColumnType.Float8 :
				element = GetFloatValue (colType);
				break;
			case TdsColumnType.FloatN :
				if (outParam) 
					comm.Skip (1);
				element = GetFloatValue (colType);
				break;
			case TdsColumnType.SmallMoney :
			case TdsColumnType.Money :
				element = GetMoneyValue (colType);
				break;
			case TdsColumnType.MoneyN :
				if (outParam)
					comm.Skip (1);
				element = GetMoneyValue (colType);
				break;
			case TdsColumnType.Numeric :
			case TdsColumnType.Decimal :
				byte precision;
				byte scale;
				if (outParam) {
					comm.Skip (1);
					precision = comm.GetByte ();
					scale = comm.GetByte ();
				}
				else {
					precision = (byte) columnInfo[ordinal]["NumericPrecision"];
					scale = (byte) columnInfo[ordinal]["NumericScale"];
				}

				element = GetDecimalValue (precision, scale);
				break;
			case TdsColumnType.DateTimeN :
				if (outParam) 
					comm.Skip (1);
				element = GetDateTimeValue (colType);
				break;
			case TdsColumnType.DateTime4 :
			case TdsColumnType.DateTime :
				element = GetDateTimeValue (colType);
				break;
			case TdsColumnType.VarBinary :
			case TdsColumnType.Binary :
				if (outParam) 
					comm.Skip (1);
				element = GetBinaryValue ();
				break;
			case TdsColumnType.BitN :
				if (outParam) 
					comm.Skip (1);
				if (comm.GetByte () == 0)
					element = null;
				else
					element = (comm.GetByte() != 0);
				break;
			case TdsColumnType.Bit :
				int columnSize = comm.GetByte ();
				element = (columnSize != 0);
				break;
			case TdsColumnType.UniqueIdentifier :
				if (comm.Peek () != 16) // If it's null, then what to do?
					break;

				len = comm.GetByte () & 0xff;
				if (len > 0) {
					byte[] guidBytes = comm.GetBytes (len, true);
					element = new Guid (guidBytes);
				}
				break;
			default :
				return null;
			}

			return element;
		}

		private object GetBinaryValue ()
		{
			int len;
			object result = null;
			if (tdsVersion == TdsVersion.tds70) {
				len = comm.GetTdsShort ();
				if (len != 0xffff && len > 0)
					result = comm.GetBytes (len, true);
			} 
			else {
				len = (comm.GetByte () & 0xff);
				if (len != 0)
					result = comm.GetBytes (len, true);
			}
			return result;
		}

		private object GetDateTimeValue (TdsColumnType type)
		{
			int len = 0;
			object result = null;
		
			switch (type) {
			case TdsColumnType.DateTime4:
				len = 4;
				break;
			case TdsColumnType.DateTime:
				len = 8;
				break;
			case TdsColumnType.DateTimeN:
				byte tmp = comm.Peek ();
				if (tmp != 0 && tmp != 4 && tmp != 8)
					break;
				len = comm.GetByte ();
				break;
			}
	
			DateTime epoch = new DateTime (1900, 1, 1);
	
			switch (len) {
			case 8 :
				result = epoch.AddDays (comm.GetTdsInt ());
				int seconds = comm.GetTdsInt ();
				long millis = ((((long) seconds) % 300L) * 1000L) / 300L;
				if (seconds != 0 || millis != 0) {
					result = ((DateTime) result).AddSeconds (seconds / 300);
					result = ((DateTime) result).AddMilliseconds (millis);
				}
				break;
			case 4 :
				result = epoch.AddDays ((int) comm.GetTdsShort ());
				short minutes = comm.GetTdsShort ();
				if (minutes != 0) 
					result = ((DateTime) result).AddMinutes ((int) minutes);
				break;
			}

			return result;
		}

		private object GetDecimalValue (byte precision, byte scale)
		{
			int[] bits = new int[4] {0,0,0,0};

			int len = (comm.GetByte() & 0xff) - 1;
			bool positive = (comm.GetByte () == 1);

			if (len < 0)
				return null;
			if (len > 16)
				throw new OverflowException ();

			for (int i = 0, index = 0; i < len && i < 16; i += 4, index += 1) 
				bits[index] = comm.GetTdsInt ();

			if (bits [3] != 0) 
				return new TdsBigDecimal (precision, scale, !positive, bits);
			else
				return new Decimal (bits[0], bits[1], bits[2], !positive, scale);
		}

		private object GetFloatValue (TdsColumnType columnType)
		{
			int columnSize = 0;
			object result = null;

			switch (columnType) {
			case TdsColumnType.Real:
				columnSize = 4;
				break;
			case TdsColumnType.Float8:
				columnSize = 8;
				break;
			case TdsColumnType.FloatN:
				columnSize = comm.GetByte ();
				break;
			}

			switch (columnSize) {
			case 8 :
				result = BitConverter.Int64BitsToDouble (comm.GetTdsInt64 ());
				break;
			case 4 :
				result = BitConverter.ToSingle (BitConverter.GetBytes (comm.GetTdsInt ()), 0);
				break;
			}

			return result;
		}

		private object GetImageValue ()
		{
			byte hasValue = comm.GetByte ();

			if (hasValue == 0)
				return null;
			
			comm.Skip (24);
			int len = comm.GetTdsInt ();

			if (len < 0)
				return null;

			return (comm.GetBytes (len, true));
		}

		private object GetIntValue (TdsColumnType type)
		{
			int len;

			switch (type) {
			case TdsColumnType.IntN :
				len = comm.GetByte ();
				break;
			case TdsColumnType.Int4 :
				len = 4; 
				break;
			case TdsColumnType.Int2 :
				len = 2; 
				break;
			case TdsColumnType.Int1 :
				len = 1; 
				break;
			default:
				return null;
			}

			switch (len) {
			case 4 :
				return (comm.GetTdsInt ());
			case 2 :
				return (comm.GetTdsShort ());
			case 1 :
				return (comm.GetByte ());
			default:
				return null;
			}
		}

		[MonoTODO]
		private object GetMoneyValue (TdsColumnType type)
		{
			int len;
			object result;

			switch (type) {
			case TdsColumnType.SmallMoney :
			case TdsColumnType.Money4 :
				len = 4;
				break;
			case TdsColumnType.Money :
				len = 8;
				break;
			case TdsColumnType.MoneyN :
				len = comm.GetByte ();
				break;
			default:
				return null;
			}

			if (len == 0)
				result = null;
			else {
				throw new NotImplementedException ();
			}

			return result;
		}

		private object GetStringValue (bool wideChars, bool outputParam)
		{
			object result = null;
			bool shortLen = (tdsVersion == TdsVersion.tds70) && (wideChars || !outputParam);

			int len = shortLen ? comm.GetTdsShort () : (comm.GetByte () & 0xff);

			if ((tdsVersion < TdsVersion.tds70 && len == 0) || (tdsVersion == TdsVersion.tds70 && len == 0xff))
				result = null;
			else if (len >= 0) {
				if (wideChars)
					result = comm.GetString (len / 2);
				else
					result = comm.GetString (len, false);
				if (tdsVersion < TdsVersion.tds70 && ((string) result).Equals (" "))
					result = "";
			}
			else
				result = null;
			return result;
		}

		private int GetSubPacketLength ()
		{
			return comm.GetTdsShort ();
		}

		private object GetTextValue (bool wideChars)
		{
			string result = null;
			byte hasValue = comm.GetByte ();

			if (hasValue != 16)
				return null;

			// 16 Byte TEXTPTR, 8 Byte TIMESTAMP
			comm.Skip (24);

			int len = comm.GetTdsInt ();

			if (len == 0)
				return null;

			if (wideChars)
				result = comm.GetString (len / 2);
			else
				result = comm.GetString (len, false);
				len /= 2;

			if ((byte) tdsVersion < (byte) TdsVersion.tds70 && result == " ")
				result = "";

			return result;
		}

		protected bool IsFixedSizeColumn (TdsColumnType columnType)
		{
			switch (columnType) {
				case TdsColumnType.Int1 :
				case TdsColumnType.Int2 :
				case TdsColumnType.Int4 :
				case TdsColumnType.Float8 :
				case TdsColumnType.DateTime :
				case TdsColumnType.Bit :
				case TdsColumnType.Money :
				case TdsColumnType.Money4 :
				case TdsColumnType.SmallMoney :
				case TdsColumnType.Real :
				case TdsColumnType.DateTime4 :
					return true;
				case TdsColumnType.IntN :
				case TdsColumnType.MoneyN :
				case TdsColumnType.VarChar :
				case TdsColumnType.NVarChar :
				case TdsColumnType.DateTimeN :
				case TdsColumnType.FloatN :
				case TdsColumnType.Char :
				case TdsColumnType.NChar :
				case TdsColumnType.NText :
				case TdsColumnType.Image :
				case TdsColumnType.VarBinary :
				case TdsColumnType.Binary :
				case TdsColumnType.Decimal :
				case TdsColumnType.Numeric :
				case TdsColumnType.BitN :
				case TdsColumnType.UniqueIdentifier :
					return false;
				default :
					return false;
			}
		}

		private TdsPacketRowResult LoadRow ()
		{
			TdsPacketRowResult result = new TdsPacketRowResult ();

			int i = 0;
			foreach (TdsSchemaInfo schema in columnInfo) {
				object o = GetColumnValue ((TdsColumnType) schema["ColumnType"], false, i);
				result.Add (o);
				if (o is TdsBigDecimal && result.BigDecimalIndex < 0) 
					result.BigDecimalIndex = i;
				i += 1;
			}

			return result;
		}

		protected int LookupBufferSize (TdsColumnType columnType)
		{
			switch (columnType) {
				case TdsColumnType.Int1 :
				case TdsColumnType.Bit :
					return 1;
				case TdsColumnType.Int2 :
					return 2;
				case TdsColumnType.Int4 :
				case TdsColumnType.Real :
				case TdsColumnType.DateTime4 :
				case TdsColumnType.Money4 :
				case TdsColumnType.SmallMoney :
					return 4;
				case TdsColumnType.Float8 :
				case TdsColumnType.DateTime :
				case TdsColumnType.Money :
					return 8;
				default :
					return 0;
			}
		}

		private int LookupDisplaySize (TdsColumnType columnType) 
		{
			switch (columnType) {
				case TdsColumnType.Int1 :
					return 3;
				case TdsColumnType.Int2 :
					return 6;
				case TdsColumnType.Int4 :
					return 11;
				case TdsColumnType.Real :
					return 14;
				case TdsColumnType.Float8 :
					return 24;
				case TdsColumnType.DateTime :
					return 23;
				case TdsColumnType.DateTime4 :
					return 16;
				case TdsColumnType.Bit :
					return 1;
				case TdsColumnType.Money :
					return 21;
				case TdsColumnType.Money4 :
				case TdsColumnType.SmallMoney :
					return 12;
				default:
					return 0;
			}
		}

		protected TdsPacketColumnInfoResult ProcessColumnDetail ()
		{
			TdsPacketColumnInfoResult result = columnInfo;
			int len = GetSubPacketLength ();
			byte[] values = new byte[3];
			int columnNameLength;
			string baseColumnName = String.Empty;
			int position = 0;

			while (position < len) {
				for (int j = 0; j < 3; j += 1) 
					values[j] = comm.GetByte ();
				position += 3;

				if ((values[2] & (byte) TdsColumnStatus.Rename) != 0) {
					if (tdsVersion == TdsVersion.tds70) {
						columnNameLength = comm.GetByte ();
						position += 2 * len + 1;
					}
					else {
						columnNameLength = comm.GetByte ();
						position += len + 1;
					}
					baseColumnName = comm.GetString (columnNameLength);
				}

				if ((values[2] & (byte) TdsColumnStatus.Hidden) == 0) {
					byte index = (byte) (values[0] - (byte) 1);
					byte tableIndex = (byte) (values[1] - (byte) 1);

					result [index]["IsExpression"] = ((values[2] & (byte) TdsColumnStatus.IsExpression) != 0);
					result [index]["IsKey"] = ((values[2] & (byte) TdsColumnStatus.IsKey) != 0);

					if ((values[2] & (byte) TdsColumnStatus.Rename) != 0)
						result [index]["BaseColumnName"] = baseColumnName;
					result [index]["BaseTableName"] = tableNames [tableIndex];
				}
			}

			return result;
		}

		protected abstract TdsPacketColumnInfoResult ProcessColumnInfo ();

		private TdsPacketColumnNamesResult ProcessColumnNames ()
		{
			TdsPacketColumnNamesResult result = new TdsPacketColumnNamesResult ();

			int totalLength = comm.GetTdsShort ();
			int bytesRead = 0;
			int i = 0;

			while (bytesRead < totalLength) {
				int columnNameLength = comm.GetByte ();
				string columnName = comm.GetString (columnNameLength);
				bytesRead = bytesRead + 1 + columnNameLength;
				result.Add (columnName);
				i += 1;
			}

			return result;
		}

		[MonoTODO ("Make sure counting works right, especially with multiple resultsets.")]
		private TdsPacketEndTokenResult ProcessEndToken (TdsPacketSubType type)
		{
			byte status = comm.GetByte ();
			comm.GetByte ();
			byte op = comm.GetByte ();
			comm.GetByte ();
			int rowCount = comm.GetTdsInt ();
			if (op == (byte) 0xc1) 
				rowCount = 0;
			if (type == TdsPacketSubType.DoneInProc) 
				rowCount = -1;

			TdsPacketEndTokenResult result = new TdsPacketEndTokenResult (type, status, rowCount);

			if (type == TdsPacketSubType.DoneProc)  {
				doneProc = true;
				if (result.RowCount > 0)
					recordsAffected += result.RowCount;
			}

			moreResults = result.MoreResults;
			FinishQuery (result.Cancelled, result.MoreResults);

			return result;
		}

		private TdsPacketResult ProcessEnvChange ()
		{
			int len = GetSubPacketLength ();
			TdsEnvPacketSubType type = (TdsEnvPacketSubType) comm.GetByte ();
			int cLen;

			switch (type) {
			case TdsEnvPacketSubType.BlockSize :
				string blockSize;
				cLen = comm.GetByte () & 0xff;
				blockSize = comm.GetString (cLen);

				if (tdsVersion == TdsVersion.tds70) 
					comm.Skip (len - 2 - cLen * 2);
				else 
					comm.Skip (len - 2 - cLen);
				
				comm.ResizeOutBuf (Int32.Parse (blockSize));
				break;
			case TdsEnvPacketSubType.CharSet :
				cLen = comm.GetByte () & 0xff;
				if (tdsVersion == TdsVersion.tds70) {
					//this.language = comm.GetString (cLen); // FIXME
					comm.GetString (cLen);
					comm.Skip (len - 2 - cLen * 2);
				}
				else {
					SetCharset (comm.GetString (cLen));
					comm.Skip (len - 2 - cLen);
				}

				break;
			case TdsEnvPacketSubType.Database :
				cLen = comm.GetByte () & 0xff;
				string newDB = comm.GetString (cLen);
				cLen = comm.GetByte () & 0xff;
				string oldDB = comm.GetString (cLen);
				database = newDB;
				break;
			default:
				comm.Skip (len - 1);
				break;
			}

			return new TdsPacketResult (TdsPacketSubType.EnvChange);
		}

		private TdsPacketResult ProcessLoginAck ()
		{
			GetSubPacketLength ();

			if (tdsVersion == TdsVersion.tds70) {
				comm.Skip (5);
				int nameLength = comm.GetByte ();
				databaseProductName = comm.GetString (nameLength);
				databaseMajorVersion = comm.GetByte ();
				databaseProductVersion = String.Format ("0{0}.0{1}.0{2}", databaseMajorVersion, comm.GetByte (), ((256 * (comm.GetByte () + 1)) + comm.GetByte ()));
			}
			else {
				comm.Skip (5);
				short nameLength = comm.GetByte ();
				databaseProductName = comm.GetString (nameLength);
				comm.Skip (1);
				databaseMajorVersion = comm.GetByte ();
				databaseProductVersion = String.Format ("{0}.{1}", databaseMajorVersion, comm.GetByte ());
				comm.Skip (1);
			}

			if (databaseProductName.Length > 1 && -1 != databaseProductName.IndexOf ('\0')) {
				int last = databaseProductName.IndexOf ('\0');
				databaseProductName = databaseProductName.Substring (0, last);
			}

			connected = true;

			return new TdsPacketResult (TdsPacketSubType.LoginAck);
		}

		protected void OnTdsErrorMessage (TdsInternalErrorMessageEventArgs e)
		{
			if (TdsErrorMessage != null)
				TdsErrorMessage (this, e);
		}

		protected void OnTdsInfoMessage (TdsInternalInfoMessageEventArgs e)
		{
			if (TdsInfoMessage != null)
				TdsInfoMessage (this, e);
			messages.Clear ();
		}
		
		private void ProcessMessage (TdsPacketSubType subType)
		{
			GetSubPacketLength ();

			int number = comm.GetTdsInt ();
			byte state = comm.GetByte ();
			byte theClass = comm.GetByte ();
			string message = comm.GetString (comm.GetTdsShort ());


			string server = comm.GetString (comm.GetByte ());

			if (subType != TdsPacketSubType.Info && subType != TdsPacketSubType.Error) 
				return;

			string procedure = comm.GetString (comm.GetByte ());
			byte lineNumber = comm.GetByte ();
			string source = String.Empty; // FIXME

			comm.GetByte ();
			if (subType == TdsPacketSubType.Error)
				OnTdsErrorMessage (CreateTdsErrorMessageEvent (theClass, lineNumber, message, number, procedure, server, source, state));
			else
				messages.Add (new TdsInternalError (theClass, lineNumber, message, number, procedure, server, source, state));
		}

		private TdsPacketOutputParam ProcessOutputParam ()
		{
			GetSubPacketLength ();
			comm.GetString (comm.GetByte () & 0xff);
			comm.Skip (5);

			TdsColumnType colType = (TdsColumnType) comm.GetByte ();
			object value = GetColumnValue (colType, true);

			outputParameters.Add (value);
			return null;
		}

		private TdsPacketResult ProcessProcId ()
		{
			comm.Skip (8);
			return new TdsPacketResult (TdsPacketSubType.ProcId);
		}

		private TdsPacketRetStatResult ProcessReturnStatus ()
		{
			return new TdsPacketRetStatResult (comm.GetTdsInt ());
		}

		protected TdsPacketResult ProcessSubPacket ()
		{
			TdsPacketResult result = null;
			moreResults = false;

			TdsPacketSubType subType = (TdsPacketSubType) comm.GetByte ();

			switch (subType) {
			case TdsPacketSubType.EnvChange :
				result = ProcessEnvChange ();
				break;
			case TdsPacketSubType.Info :
			case TdsPacketSubType.Msg50Token :
			case TdsPacketSubType.Error :
				ProcessMessage (subType);
				break;
			case TdsPacketSubType.Param :
				result = ProcessOutputParam ();
				break;
			case TdsPacketSubType.LoginAck :
				result = ProcessLoginAck ();
				break;
			case TdsPacketSubType.ReturnStatus :
				result = ProcessReturnStatus ();
				break;
			case TdsPacketSubType.ProcId :
				result = ProcessProcId ();
				break;
			case TdsPacketSubType.Done :
			case TdsPacketSubType.DoneProc :
			case TdsPacketSubType.DoneInProc :
				result = ProcessEndToken (subType);
				break;
			case TdsPacketSubType.ColumnNameToken :
				result = ProcessProcId ();
				result = ProcessColumnNames ();
				break;
			case TdsPacketSubType.ColumnInfoToken :
			case TdsPacketSubType.ColumnMetadata :
				result = ProcessColumnInfo ();
				break;
			case TdsPacketSubType.ColumnDetail :
				result = ProcessColumnDetail ();
				break;
			case TdsPacketSubType.Unknown0xA7 :
			case TdsPacketSubType.Unknown0xA8 :
				comm.Skip (comm.GetTdsShort ());
				result = new TdsPacketUnknown (subType);
				break;
			case TdsPacketSubType.TableName :
				result = ProcessTableName ();
				break;
			case TdsPacketSubType.Order :
				comm.Skip (comm.GetTdsShort ());
				result = new TdsPacketColumnOrderResult ();
				break;
			case TdsPacketSubType.Control :
				comm.Skip (comm.GetTdsShort ());
				result = new TdsPacketControlResult ();
				break;
			case TdsPacketSubType.Row :
				result = LoadRow ();
				break;
			default:
				return null;
			}

			return result;
		}

		private TdsPacketTableNameResult ProcessTableName ()
		{
			TdsPacketTableNameResult result = new TdsPacketTableNameResult ();
			int totalLength = comm.GetTdsShort ();
			int position = 0;
			int len;

			while (position < totalLength) {
				if (tdsVersion == TdsVersion.tds70) {
					len = comm.GetTdsShort ();
					position += 2 * (len + 1);
				}
				else {
					len = comm.GetByte ();
					position += len + 1;
				}
				result.Add (comm.GetString (len));
			}	
			return result;
		}

		protected void SetCharset (string charset)
		{
			if (charset == null || charset.Length > 30)
				charset = "iso_1";

			if (this.charset != null && this.charset != charset)
				return;

			if (charset.StartsWith ("cp")) {
				encoder = Encoding.GetEncoding (Int32.Parse (charset.Substring (2)));
				this.charset = charset;
			}
			else {
				encoder = Encoding.GetEncoding ("iso-8859-1");
				this.charset = "iso_1";
			}
			comm.Encoder = encoder;
		}

		protected void SetLanguage (string language)
		{
			if (language == null || language.Length > 30)
				language = "us_english";

			this.language = language;
		}

		#endregion // Private Methods
	}
}
