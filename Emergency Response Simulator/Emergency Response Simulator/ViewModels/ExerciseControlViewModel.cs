using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emergency_Response_Simulator.Client;
using Emergency_Response_Simulator.Core.Events;

namespace Emergency_Response_Simulator.ViewModels;

/// <summary>
/// Exercise control messages (Phase 12): what the instructor says to the players outside the world, shown as a banner
/// across the main window until dismissed. Messages to someone else are not shown. Messages sent before this client
/// joined are history, not news, so only those that arrive live are shown.
/// </summary>
public partial class ExerciseControlViewModel : ObservableObject
{
    private readonly SimulationConnection? _connection;

    public ExerciseControlViewModel(IEventStore store, SimulationConnection? connection)
    {
        _connection = connection;
        store.Appended += (_, simEvent) =>
        {
            if (simEvent.Payload is ExerciseControlMessage message && ForMe(message) && simEvent.WallTime >= DateTimeOffset.UtcNow.AddMinutes(-2))
                Application.Current?.Dispatcher.BeginInvoke(() => Show(message));
        };
    }

    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _message = "";

    private bool ForMe(ExerciseControlMessage message) =>
        message.ToParticipantId is not { } to || to == _connection?.Ticket?.ParticipantId;

    private void Show(ExerciseControlMessage message)
    {
        Message = (message.ToParticipantId is null ? "EXERCISE CONTROL: " : "EXERCISE CONTROL (to you): ") + message.Text;
        HasMessage = true;
    }

    [RelayCommand]
    private void Dismiss() => HasMessage = false;
}
