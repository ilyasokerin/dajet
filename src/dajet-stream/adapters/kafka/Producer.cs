﻿using Confluent.Kafka;
using DaJet.Scripting.Model;
using System.Text;
using Error = Confluent.Kafka.Error;

namespace DaJet.Stream.Kafka
{
    public sealed class Producer : IProcessor
    {
        private IProcessor _next;
        private readonly StreamScope _scope;
        private readonly ProduceStatement _options;

        private int _produced;
        private string _error;
        private string _topic;
        private ProducerConfig _config;
        private IProducer<byte[], byte[]> _producer;
        private readonly Message<byte[], byte[]> _message = new();
        private readonly Action<IProducer<byte[], byte[]>, Error> _errorHandler;
        private readonly Action<IProducer<byte[], byte[]>, LogMessage> _logHandler;
        private readonly Action<DeliveryReport<byte[], byte[]>> _deliveryReportHandler;
        private static string GetOptionKey(in string name)
        {
            StringBuilder key = new();

            for (int i = 0; i < name.Length; i++)
            {
                char chr = name[i];

                if (char.IsUpper(chr))
                {
                    if (i > 0) { key.Append('.'); }

                    key.Append(char.ToLowerInvariant(chr));
                }
                else
                {
                    key.Append(chr);
                }
            }

            return key.ToString();
        }
        public Producer(in StreamScope scope)
        {
            _scope = scope ?? throw new ArgumentNullException(nameof(scope));

            if (_scope.Owner is not ProduceStatement statement)
            {
                throw new ArgumentException(nameof(ProduceStatement));
            }

            _options = statement;
            
            StreamFactory.MapOptions(in _scope);

            _logHandler = LogHandler;
            _errorHandler = ErrorHandler;
            _deliveryReportHandler = HandleDeliveryReport;
        }
        public void LinkTo(in IProcessor next) { _next = next; }
        public void Process()
        {
            _config ??= CreateProducerConfig();

            _producer ??= new ProducerBuilder<byte[], byte[]>(_config)
                .SetLogHandler(_logHandler)
                .SetErrorHandler(_errorHandler)
                .Build();

            try
            {
                _topic = GetTopic();
                _message.Key = GetKey();
                _message.Value = GetValue();

                _producer.Produce(_topic, _message, _deliveryReportHandler); // async inside - returns immediately
            }
            catch
            {
                Dispose(); throw;
            }
        }
        private ProducerConfig CreateProducerConfig()
        {
            Dictionary<string, string> config = new();

            foreach (var option in _scope.Variables)
            {
                string key = GetOptionKey(option.Key);

                if (key == "key" || key == "value" || key == "topic")
                {
                    continue; // message values
                }

                if (StreamFactory.TryGetOption(in _scope, option.Key, out object value))
                {
                    config.Add(key, value.ToString());
                }
            }

            return new ProducerConfig(config);
        }
        private byte[] GetKey()
        {
            if (StreamFactory.TryGetOption(in _scope, "Key", out object value))
            {
                if (value is byte[] binary)
                {
                    return binary;
                }
                else if(value is string text && !string.IsNullOrEmpty(text))
                {
                    return Encoding.UTF8.GetBytes(text);
                }
            }

            return null;
        }
        private byte[] GetValue()
        {
            if (StreamFactory.TryGetOption(in _scope, "Value", out object value))
            {
                if (value is byte[] binary)
                {
                    return binary;
                }
                else if (value is string text && !string.IsNullOrEmpty(text))
                {
                    return Encoding.UTF8.GetBytes(text);
                }
            }

            return Array.Empty<byte>();
        }
        private string GetTopic()
        {
            if (StreamFactory.TryGetOption(in _scope, "Topic", out object value))
            {
                return value.ToString();
            }

            return string.Empty;
        }
        private void LogHandler(IProducer<byte[], byte[]> _, LogMessage message)
        {
            Console.WriteLine($"[{_topic}] [{message.Name}]: {message.Message}");
            //FileLogger.Default.Write($"[{_topic}] [{message.Name}]: {message.Message}");
        }
        private void ErrorHandler(IProducer<byte[], byte[]> _, Error error)
        {
            if (error is not null)
            {
                _error = error.Reason;
            }

            Console.WriteLine($"[{_topic}] [{_config.ClientId}]: {error?.Reason}");
            //FileLogger.Default.Write($"[{_topic}] [{_config.ClientId}]: {error?.Reason}");
        }
        private void HandleDeliveryReport(DeliveryReport<byte[], byte[]> report)
        {
            if (report.Status == PersistenceStatus.Persisted)
            {
                Interlocked.Increment(ref _produced);
            }
            else if (report.Error is not null && report.Error.Code != ErrorCode.NoError)
            {
                _error = report.Error.Reason; //THINK: stop producing the batch !?

                Console.WriteLine($"[{report.Topic}] [{_config.ClientId}]: {report.Error.Reason}");
                //FileLogger.Default.Write($"[{report.Topic}] [{_config.ClientId}]: {report.Error.Reason}");
            }
        }
        public void Synchronize()
        {
            int produced = _produced;

            try
            {
                _producer?.Flush(); // synchronously wait for pending work to complete
            }
            catch
            {
                throw;
            }
            finally
            {
                Dispose(); _produced = 0; //THINK: _next?.Synchronize(); ???
            }

            if (_error is not null)
            {
                string error = _error;
                _error = null;
                throw new InvalidOperationException(error);
            }
            else
            {
                Console.WriteLine($"[{_config.ClientId}] Produced {produced} messages");
            }
        }
        public void Dispose()
        {
            try
            {
                _producer?.Dispose();
            }
            finally
            {
                _producer = null; //THINK: _next?.Dispose(); ???
            }
        }
    }
}