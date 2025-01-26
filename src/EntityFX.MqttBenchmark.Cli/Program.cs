using EntityFX.MqttBenchmark;
using System.CommandLine;
using System.CommandLine.Binding;

var settingsBinder = new SettingsBinder(
    hostArgument : new Argument<string>("host", "Mqtt host (IP or domain adress with port), e.g. mqtt://localhost:18883"), 
    topicOption : new Option<string>(new string[] { "-t", "--topic" }, "Topic"),
    qosOption : new Option<int>(new string[] { "-q", "--qos" }, () => 1, "QoS (Quality of Service)"),
    clientsOption : new Option<int>(new string[] { "-c", "--clients" }, () => 10, "Clients count to simulate"),
    payloadOption: new Option<string>(new string[] { "-l", "--payload" }, "Payload value as stings"),
    maxMessageSizeOption: new Option<int>(new string[] { "-s", "--max-size" }, () => 1024, "Max message size in bytes"),
    messageCountOption: new Option<int>(new string[] { "-m", "--message-count" }, () => 10, "Max messages for benchmark"),
    userOption: new Option<string>(new string[] { "-u", "--username" }, "Mqtt auth user"),
    passwordOption: new Option<string>(new string[] { "-p", "--password" }, "Mqtt auth password")
);

var rootCommand = settingsBinder.GetRootCommand();

rootCommand.SetHandler((settings) =>
{
    //DoRootCommand(fileOptionValue, person);
}, settingsBinder);

rootCommand.Invoke(args);
Console.ReadKey();

public class SettingsBinder : BinderBase<Settings>
{
    private readonly Argument<string> _hostArgument;
    private readonly Option<string> _topicOption;
    private readonly Option<int> _qosOption;
    private readonly Option<int> _clientsOption;
    private readonly Option<string> _payloadOption;
    private readonly Option<int> _maxMessageSizeOption;
    private readonly Option<int> _messageCountOption;
    private readonly Option<string> _userOption;
    private readonly Option<string> _passwordOption;

    public SettingsBinder(
        Argument<string> hostArgument, 
        Option<string> topicOption, 
        Option<int> qosOption,
        Option<int> clientsOption,
        Option<string> payloadOption,
        Option<int> maxMessageSizeOption,
        Option<int> messageCountOption,
        Option<string> userOption,
        Option<string> passwordOption)
    {
        _hostArgument = hostArgument;
        _topicOption = topicOption;
        _qosOption = qosOption;
        _clientsOption = clientsOption;
        _payloadOption = payloadOption;
        _maxMessageSizeOption = maxMessageSizeOption;
        _messageCountOption = messageCountOption;
        _userOption = userOption;
        _passwordOption = passwordOption;
    }

    public RootCommand GetRootCommand()
    {
        return new RootCommand
        {
            _hostArgument,
            _topicOption,
            _qosOption,
            _clientsOption,
            _payloadOption,
            _maxMessageSizeOption,
            _messageCountOption,
            _userOption,
            _passwordOption
        };
    }

    protected override Settings GetBoundValue(BindingContext bindingContext)
    {
        return new Settings
        {
            Broker = new Uri(bindingContext.ParseResult.GetValueForArgument(_hostArgument)),
            Topic = bindingContext.ParseResult.GetValueForOption(_topicOption),
            Qos = bindingContext.ParseResult.GetValueForOption(_qosOption),
            Clients = bindingContext.ParseResult.GetValueForOption(_clientsOption),
            Payload = bindingContext.ParseResult.GetValueForOption(_payloadOption),
            MessageSize = bindingContext.ParseResult.GetValueForOption(_maxMessageSizeOption),
            MessageCount = bindingContext.ParseResult.GetValueForOption(_messageCountOption),
            Username = bindingContext.ParseResult.GetValueForOption(_userOption),
            Password = bindingContext.ParseResult.GetValueForOption(_passwordOption),
        };
    }
}