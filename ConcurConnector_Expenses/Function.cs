using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Newtonsoft.Json.Linq;
using MySql.Data.MySqlClient;
using System.Net.Http;
using Newtonsoft.Json;
using MySql.Data.MySqlClient;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace ConcurConnector_Expenses
{
 /* Author: M Pierson
  * Date: 10/30/2020
  * Version 1.0
  * This solution retrieves approved expenses from Concur that are 14 days or older and passes them to the customers Cloud database.
  * The store procedure will check to see if the reports have already been processed then insert new reports along with the report details
  * 
  */

    public class Function
    {

    /// <summary>
    /// A simple function that takes a string and does a ToUpper
    /// </summary>
    /// <param name="input"></param>
    /// <param name="context"></param>
    /// <returns></returns>



    public class Token
        {
            public int expires_in { get; set; }
            public string scope { get; set; }
            public string token_type { get; set; }
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public int refresh_expires_in { get; set; }
            public string geolocation { get; set; }
            public string id_token { get; set; }
        }

        public class ConfigInfo
        {
            public string client_secret { get; set; }
            public string user_name { get; set; }
            public string password { get; set; }
            public string client_id { get; set; }
        }

    public string Encrypt(string textToEncrypt)
    {
     //Standard Encrypt/Decrypt routines
        try
        {
            string ToReturn = "";
            string _key = "ASDF@dfarDfg134#$%^@%D";
            string _iv = ";rgeth%#^^#FGRHW$FWHWERT";
            byte[] _ivByte = { };
            _ivByte = System.Text.Encoding.UTF8.GetBytes(_iv.Substring(0, 8));
            byte[] _keybyte = { };
            _keybyte = System.Text.Encoding.UTF8.GetBytes(_key.Substring(0, 8));
            MemoryStream ms = null; CryptoStream cs = null;
            byte[] inputbyteArray = System.Text.Encoding.UTF8.GetBytes(textToEncrypt);
            using (DESCryptoServiceProvider des = new DESCryptoServiceProvider())
            {
                ms = new MemoryStream();
                cs = new CryptoStream(ms, des.CreateEncryptor(_keybyte, _ivByte), CryptoStreamMode.Write);
                cs.Write(inputbyteArray, 0, inputbyteArray.Length);
                cs.FlushFinalBlock();
                ToReturn = Convert.ToBase64String(ms.ToArray());
            }
            return ToReturn;
        }
        catch (Exception ae)
        {
            return "Error Encrypting Password";
        }
    }

    public string Decrypt(string textToDecrypt)
    {
        try
        {
            string ToReturn = "";
            string _key = "ASDF@dfarDfg134#$%^@%D";
            string _iv = ";rgeth%#^^#FGRHW$FWHWERT";
            byte[] _ivByte = { };
            _ivByte = System.Text.Encoding.UTF8.GetBytes(_iv.Substring(0, 8));
            byte[] _keybyte = { };
            _keybyte = System.Text.Encoding.UTF8.GetBytes(_key.Substring(0, 8));
            MemoryStream ms = null; CryptoStream cs = null;
            byte[] inputbyteArray = new byte[textToDecrypt.Replace(" ", "+").Length];
            inputbyteArray = Convert.FromBase64String(textToDecrypt.Replace(" ", "+"));
            using (DESCryptoServiceProvider des = new DESCryptoServiceProvider())
            {
                ms = new MemoryStream();
                cs = new CryptoStream(ms, des.CreateDecryptor(_keybyte, _ivByte), CryptoStreamMode.Write);
                cs.Write(inputbyteArray, 0, inputbyteArray.Length);
                cs.FlushFinalBlock();
                Encoding encoding = Encoding.UTF8;
                ToReturn = encoding.GetString(ms.ToArray());
            }
            return ToReturn;
        }
        catch (Exception ae)
        {

            return "Error Decrypting Password";
        }


    }
    public APIGatewayProxyResponse FunctionHandler(JObject input, ILambdaContext context)
        {

            try
            {
                string envID = "ConcurLive"; //Used to pull enivronment settings. Hard coded for THK for now. Can change in future versions
                string firstSubmitDate = "2020-11-01";//THK Beginning of life for this integration
                string nextSubmitDate = "";//used to calculate criteria for web call

                Token tokenResponse = new Token();
                string requestUrl = $"https://us.api.concursolutions.com/oauth2/v0/token";
                var httpClientHandler = new HttpClientHandler();
                httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => { return true; };


                string myConnectionString = "server=db6.cqc3tpt63rhe.us-west-1.rds.amazonaws.com;uid=StellarAdmin;pwd=Stellar1c;database=HonestKitchen";
                using (MySql.Data.MySqlClient.MySqlConnection conn = new MySql.Data.MySqlClient.MySqlConnection())
                {
                    conn.ConnectionString = myConnectionString;
                    Console.WriteLine("Opening Connection");
                    conn.Open();
                    var configInfoCmd = conn.CreateCommand();
                    configInfoCmd.CommandType= CommandType.Text;
                    configInfoCmd.CommandText = $"Select ConfigurationInfo from Environments Where EnvKey='{envID}'";
                    string sConfigInfo = configInfoCmd.ExecuteScalar().ToString();
                    ConfigInfo configInfo = JsonConvert.DeserializeObject<ConfigInfo>(sConfigInfo);

                    //Get highest submitted date from staging and subract 14 days
                    #region Get Last Submitted Date
                    using (MySql.Data.MySqlClient.MySqlConnection conn1 = new MySql.Data.MySqlClient.MySqlConnection())
                    {

                        conn1.ConnectionString = myConnectionString;
                        Console.WriteLine("Opening Connection");
                        conn1.Open();
                        string dateSQL = $"Select coalesce(date_add(Max(SubmittedDate), INTERVAL -14 DAY),'{firstSubmitDate}') CutOff   From Concur_ExpenseReports";
                        using (var cmd = conn1.CreateCommand())
                        {


                            cmd.CommandType = CommandType.Text;
                            cmd.CommandText = dateSQL;
                            using (var sqlResult = cmd.ExecuteReaderAsync())
                            {

                                sqlResult.Wait(0);
                                sqlResult.Result.Read();

                                nextSubmitDate = sqlResult.Result.GetValue(sqlResult.Result.GetOrdinal("CutOff")).ToString();
                            }
                        }
                    }
                    #endregion

                    #region Get Token from Concur for subsequent calls
                    var client = new HttpClient(httpClientHandler);
                    {

                        //Setup API call and retrieve token
                        Console.WriteLine("Starting Web Request");
                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                        var nvc = new List<KeyValuePair<string, string>>();
                        string clientSecret = Decrypt(configInfo.client_secret);
                        string grantType = "password";
                        string userName = configInfo.user_name;
                        string password = Decrypt(configInfo.password);
                        string credType = "password";
                        string clientID = configInfo.client_id;
                        nvc.Add(new KeyValuePair<string, string>("client_secret", clientSecret));
                        nvc.Add(new KeyValuePair<string, string>("grant_type", grantType));
                        nvc.Add(new KeyValuePair<string, string>("username", userName));
                        nvc.Add(new KeyValuePair<string, string>("password", password));
                        nvc.Add(new KeyValuePair<string, string>("credtype", credType));
                        nvc.Add(new KeyValuePair<string, string>("client_id", clientID));
                        request.Content = new FormUrlEncodedContent(nvc);
                        request.RequestUri = new Uri(requestUrl);
                        Task<HttpResponseMessage> content = client.PostAsync(request.RequestUri, request.Content);
                        content.Wait();
                        Task<string> apiResponse = content.Result.Content.ReadAsStringAsync();
                        tokenResponse = JsonConvert.DeserializeObject<Token>(apiResponse.Result.ToString());
                    }
                    #endregion

                    #region Get Reports
                    {
                        //Setup and call Get Reports API  
                        requestUrl = "https://www.concursolutions.com/api/v3.0/expense/reports";
                        requestUrl += $"?limit=100&user=ALL&ApprovalStatusCode=A_APPR&submitDateAfter={nextSubmitDate}";
                        HttpRequestMessage getReports = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                        getReports.RequestUri = new Uri(requestUrl);
                        getReports.Headers.Add("Accept", "application/json");
                        getReports.Headers.Add("Content", "application/json");
                        getReports.Headers.Add("Authorization", "Bearer " + tokenResponse.access_token);

                        Task<HttpResponseMessage> content = client.SendAsync(getReports);
                        content.Wait();
                        Task<string> apiResponse = content.Result.Content.ReadAsStringAsync();
                        
                        try
                        {
                            dynamic json = JsonConvert.DeserializeObject(apiResponse.Result.ToString());
                            //Loop through reports to get report detail and pass to database
                            foreach (dynamic reportJSON in json.Items)
                            {
                                
                                string reportID = reportJSON.ID;
                                string strReportJSON = reportJSON.ToString();
                                #region Get report details and insert to DB
                                Console.WriteLine(reportID.ToString());
                                requestUrl = $"https://www.concursolutions.com/api/expense/expensereport/v2.0/report/{reportID}";
                                HttpRequestMessage getReportLines = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                                getReportLines.RequestUri = new Uri(requestUrl);
                                getReportLines.Headers.Add("Accept", "application/json");
                                getReportLines.Headers.Add("Content", "application/json");
                                getReportLines.Headers.Add("Authorization", "Bearer " + tokenResponse.access_token);
                                
                                Task<HttpResponseMessage> linecontent = client.SendAsync(getReportLines);
                                
                                linecontent.Wait(120000);
                                Task<string> apiLineResponse = linecontent.Result.Content.ReadAsStringAsync();
                                string linejson = apiLineResponse.Result.ToString();
                                Console.WriteLine("Report Retrieved");

                                var myTrans = conn.BeginTransaction();
                                var cmdSQL = conn.CreateCommand();
                                cmdSQL.CommandType = CommandType.StoredProcedure;
                                cmdSQL.CommandText = "Concur_Expenses_Insert";
                                cmdSQL.Parameters.AddWithValue("_ReportID", reportID.ToString());
                                cmdSQL.Parameters.AddWithValue("_ReportName", reportJSON.Name.ToString());
                                cmdSQL.Parameters.AddWithValue("_CreatedDate", reportJSON.CreateDate.ToString());
                                cmdSQL.Parameters.AddWithValue("_SubmittedDate", reportJSON.SubmitDate.ToString());
                                cmdSQL.Parameters.AddWithValue("_EnvID", envID.ToString());
                                cmdSQL.Parameters.AddWithValue("_ReportHeader", strReportJSON.ToString());
                                cmdSQL.Parameters.AddWithValue("_ReportDetail", linejson.ToString());
                                Console.WriteLine("Starting Insert");
                                cmdSQL.ExecuteNonQuery();
                                Console.WriteLine("End Insert");
                                #endregion

                            }

                        }
                        #endregion
                        catch (Exception ex)
                        {
                            Console.Write(ex);
                        }

                    }

                }

                var resp = new APIGatewayProxyResponse
                {
                    StatusCode = 200,
                    //Body = input.ToString()
                };
                return resp;
            }
            catch (Exception ex)
            {
                var resp = new APIGatewayProxyResponse
                {
                    StatusCode = 400,
                    //Body = input.ToString()
                };
                return resp;
            }
        }
    }
}
