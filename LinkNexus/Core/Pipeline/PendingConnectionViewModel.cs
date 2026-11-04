using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace LinkNexus.Core.Pipeline
{
    /// <summary>
    /// 对齐 Nodify MVVM 规范的拖拽吸附中悬挂连线视图模型 (PendingConnectionViewModel)
    /// </summary>
    public class PendingConnectionViewModel : INotifyPropertyChanged
    {
        private ConnectorViewModel? _source;
        private Point _targetAnchor;
        private bool _isVisible;

        public ConnectorViewModel? Source
        {
            get => _source;
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged();
                    IsVisible = _source != null;
                }
            }
        }

        public Point TargetAnchor
        {
            get => _targetAnchor;
            set
            {
                if (!_targetAnchor.Equals(value))
                {
                    _targetAnchor = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand StartCommand { get; }
        public ICommand FinishCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action<ConnectorViewModel>? ConnectionStarted;
        public event Action<ConnectorViewModel, object?>? ConnectionCompleted;
        public event Action? ConnectionCancelled;
        public event PropertyChangedEventHandler? PropertyChanged;

        public PendingConnectionViewModel()
        {
            StartCommand = new RelayPendingCommand(p =>
            {
                if (p is ConnectorViewModel connector)
                {
                    Source = connector;
                    ConnectionStarted?.Invoke(connector);
                }
            });

            FinishCommand = new RelayPendingCommand(p =>
            {
                if (Source != null)
                {
                    ConnectionCompleted?.Invoke(Source, p);
                }
                Source = null;
                IsVisible = false;
            });

            CancelCommand = new RelayPendingCommand(_ =>
            {
                Source = null;
                IsVisible = false;
                ConnectionCancelled?.Invoke();
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class RelayPendingCommand : ICommand
        {
            private readonly Action<object?> _act;
            public RelayPendingCommand(Action<object?> act) => _act = act;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _act(parameter);
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}

