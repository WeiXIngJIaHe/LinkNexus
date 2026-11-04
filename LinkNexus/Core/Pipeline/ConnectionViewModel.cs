using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkNexus.Core.Pipeline
{
    /// <summary>
    /// 对齐 Nodify MVVM 规范的贝塞尔连线视图模型 (ConnectionViewModel)
    /// </summary>
    public class ConnectionViewModel : INotifyPropertyChanged
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public ConnectorViewModel Output { get; }
        public ConnectorViewModel Input { get; }

        public NodeBase? SourceNode => Output.OwnerNode;
        public NodeBase? TargetNode => Input.OwnerNode;

        public Brush WireBrush => Output.PinBrush;

        public ICommand DisconnectCommand { get; }

        public event Action<ConnectionViewModel>? DisconnectRequested;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionViewModel(ConnectorViewModel output, ConnectorViewModel input)
        {
            Output = output ?? throw new ArgumentNullException(nameof(output));
            Input = input ?? throw new ArgumentNullException(nameof(input));

            DisconnectCommand = new RelayConnCommand(() => DisconnectRequested?.Invoke(this));
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class RelayConnCommand : ICommand
        {
            private readonly Action _act;
            public RelayConnCommand(Action act) => _act = act;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _act();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}

