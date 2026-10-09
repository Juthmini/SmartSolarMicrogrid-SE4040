/*
 * File Name: ReservationServiceTests.cs
 * Project: Smart Solar Microgrid Trading System
 * Module: SE4040 Enterprise Application Development
 * Author: IT22124708
 * Description: Unit tests for the reservation business rules in ReservationService
 *              (7-day booking window, 12-hour update/cancel rule, slot availability,
 *              ownership checks and status changes). The MongoDB repositories are
 *              replaced with Moq mocks, so no database is needed.
 * Date: 2026-10-12
 */

using Moq;
using SmartSolarMicrogrid.Api.Models.DTOs.Reservations;
using SmartSolarMicrogrid.Api.Models.Entities.Reservations;
using SmartSolarMicrogrid.Api.Models.Entities.Stations;
using SmartSolarMicrogrid.Api.Models.Enums.Reservations;
using SmartSolarMicrogrid.Api.Models.Enums.Stations;
using SmartSolarMicrogrid.Api.Repositories.Reservations;
using SmartSolarMicrogrid.Api.Repositories.Stations;
using SmartSolarMicrogrid.Api.Services.Reservations;

namespace SmartSolarMicrogrid.Api.Tests;

public class ReservationServiceTests
{
    // Fake repositories (no real database)
    private readonly Mock<IEnergyReservationRepository> _reservationRepo = new();
    private readonly Mock<IEnergyBookingSlotRepository> _slotRepo = new();
    private readonly Mock<ISolarStationInfoRepository> _stationRepo = new();

    // The real service we are testing
    private readonly ReservationService _service;

    private const string StationId = "station-1";
    private const string SlotId = "slot-1";
    private const string OtherSlotId = "slot-2";
    private const string ProsumerNic = "200012345678";
    private const string OtherNic = "199912345678";

    // Runs before every test: builds the service and sets safe default answers for the fakes.
    public ReservationServiceTests()
    {
        _service = new ReservationService(_reservationRepo.Object, _slotRepo.Object, _stationRepo.Object);

        // By default there are no reservations in the database.
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>());
        _slotRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyBookingSlot>());

        // By default the station exists.
        var station = MakeStation();
        _stationRepo.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(station);
        _stationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<SolarStationInfo> { station });
    }

    // ------------------------------------------------------------------
    // Helper methods that build test data
    // ------------------------------------------------------------------

    // Builds a station object used by the fake station repository.
    private static SolarStationInfo MakeStation() => new()
    {
        StationId = StationId,
        StationName = "Colombo Solar Hub",
        Address = "Colombo",
        OperatingStartTime = "08:00",
        OperatingEndTime = "18:00",
        Status = StationStatus.ACTIVE
    };

    // Builds a one-hour slot that starts at the given time.
    private static EnergyBookingSlot MakeSlot(DateTime start, SlotStatus status = SlotStatus.AVAILABLE, string slotId = SlotId) => new()
    {
        SlotId = slotId,
        SlotName = "Slot " + slotId,
        StationId = StationId,
        StartDateTime = start,
        EndDateTime = start.AddHours(1),
        Capacity = 50,
        Status = status
    };

    // Builds a reservation that starts at the given time.
    private static EnergyReservation MakeReservation(DateTime start, ReservationStatus status,
        string nic = ProsumerNic, string slotId = SlotId, string id = "res-1") => new()
    {
        ReservationId = id,
        ReservationNumber = "RES-12345",
        ProsumerNic = nic,
        StationId = StationId,
        SlotId = slotId,
        ScheduledStartDateTime = start,
        ScheduledEndDateTime = start.AddHours(1),
        Status = status,
        QrReference = "qr-" + id,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // Builds the request a prosumer sends to create a booking.
    private static CreateReservationDto MakeCreateRequest(string slotId = SlotId, string stationId = StationId) => new()
    {
        StationId = stationId,
        SlotId = slotId,
        BookingDate = "",
        StartTime = "",
        Notes = "Test booking"
    };

    // ------------------------------------------------------------------
    // CREATE reservation - 7-day rule and slot availability
    // ------------------------------------------------------------------

    // A free slot 2 days from now is valid: a PENDING reservation is created and the slot becomes PENDING.
    [Fact]
    public async Task CreateReservation_ValidSlotWithin7Days_CreatesPendingReservation()
    {
        var slot = MakeSlot(DateTime.UtcNow.AddDays(2));
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.True(result.Success);
        Assert.NotNull(result.Reservation);
        Assert.Equal(ReservationStatus.PENDING, result.Reservation!.Status);
        Assert.Equal(ProsumerNic, result.Reservation.ProsumerNic);
        Assert.Equal("Colombo Solar Hub", result.Reservation.StationName);
        Assert.False(string.IsNullOrEmpty(result.Reservation.QrReference));
        Assert.Equal(SlotStatus.PENDING, slot.Status);
        _reservationRepo.Verify(r => r.CreateAsync(It.IsAny<EnergyReservation>()), Times.Once);
    }

    // A slot on day 7 is still inside the allowed window.
    [Fact]
    public async Task CreateReservation_SlotOnDay7_IsAllowed()
    {
        var slot = MakeSlot(DateTime.UtcNow.Date.AddDays(7).AddHours(10));
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.True(result.Success);
    }

    // A slot 8 days away breaks the "within 7 days" rule.
    [Fact]
    public async Task CreateReservation_SlotMoreThan7DaysAhead_IsRejected()
    {
        var slot = MakeSlot(DateTime.UtcNow.Date.AddDays(8).AddHours(10));
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.False(result.Success);
        Assert.Contains("7 days", result.Message);
        _reservationRepo.Verify(r => r.CreateAsync(It.IsAny<EnergyReservation>()), Times.Never);
    }

    // A slot that already started cannot be booked.
    [Fact]
    public async Task CreateReservation_SlotInThePast_IsRejected()
    {
        var slot = MakeSlot(DateTime.UtcNow.AddHours(-1));
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.False(result.Success);
        _reservationRepo.Verify(r => r.CreateAsync(It.IsAny<EnergyReservation>()), Times.Never);
    }

    // A slot that is not AVAILABLE (already reserved) cannot be booked.
    [Fact]
    public async Task CreateReservation_SlotNotAvailable_IsRejected()
    {
        var slot = MakeSlot(DateTime.UtcNow.AddDays(2), SlotStatus.RESERVED);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.False(result.Success);
        Assert.Contains("not available", result.Message);
    }

    // A slot that another prosumer already holds (APPROVED reservation) cannot be double-booked.
    [Fact]
    public async Task CreateReservation_SlotAlreadyBookedByAnotherProsumer_IsRejected()
    {
        var start = DateTime.UtcNow.AddDays(2);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(MakeSlot(start));
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>
        {
            MakeReservation(start, ReservationStatus.APPROVED, nic: OtherNic)
        });

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.False(result.Success);
        Assert.Contains("already", result.Message);
        _reservationRepo.Verify(r => r.CreateAsync(It.IsAny<EnergyReservation>()), Times.Never);
    }

    // The slot must belong to the station in the request.
    [Fact]
    public async Task CreateReservation_SlotFromDifferentStation_IsRejected()
    {
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(MakeSlot(DateTime.UtcNow.AddDays(2)));

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest(stationId: "station-999"));

        Assert.False(result.Success);
        Assert.Contains("mismatch", result.Message);
    }

    // A slot id that does not exist is rejected.
    [Fact]
    public async Task CreateReservation_SlotDoesNotExist_IsRejected()
    {
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync((EnergyBookingSlot)null!);

        var result = await _service.CreateReservationAsync(ProsumerNic, MakeCreateRequest());

        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------
    // UPDATE reservation - 12-hour rule, ownership, status
    // ------------------------------------------------------------------

    // Less than 12 hours before the start, updates are blocked.
    [Fact]
    public async Task UpdateReservation_Within12Hours_IsRejected()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddHours(11), ReservationStatus.PENDING);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);

        var request = new UpdateReservationDto { SlotId = OtherSlotId, Notes = "change" };
        var result = await _service.UpdateReservationAsync("res-1", ProsumerNic, "PROSUMER", request);

        Assert.False(result.Success);
        Assert.Contains("12 hours", result.Message);
        _reservationRepo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<EnergyReservation>()), Times.Never);
    }

    // More than 12 hours before the start, the prosumer can move the booking to another free slot.
    [Fact]
    public async Task UpdateReservation_MoreThan12Hours_MovesToNewSlot()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.PENDING);
        var oldSlot = MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.PENDING, SlotId);
        var newSlot = MakeSlot(DateTime.UtcNow.AddDays(3), SlotStatus.AVAILABLE, OtherSlotId);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation> { reservation });
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(oldSlot);
        _slotRepo.Setup(r => r.GetByIdAsync(OtherSlotId)).ReturnsAsync(newSlot);

        var request = new UpdateReservationDto { SlotId = OtherSlotId, Notes = "moved" };
        var result = await _service.UpdateReservationAsync("res-1", ProsumerNic, "PROSUMER", request);

        Assert.True(result.Success);
        Assert.Equal(OtherSlotId, result.Reservation!.SlotId);
        Assert.Equal(SlotStatus.AVAILABLE, oldSlot.Status);   // old slot is freed
        Assert.Equal(SlotStatus.PENDING, newSlot.Status);     // new slot is held
        _reservationRepo.Verify(r => r.UpdateAsync("res-1", It.IsAny<EnergyReservation>()), Times.Once);
    }

    // Changing an APPROVED booking sends it back to PENDING for re-approval.
    [Fact]
    public async Task UpdateReservation_ApprovedBookingChanged_GoesBackToPending()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.APPROVED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation> { reservation });
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.RESERVED, SlotId));
        _slotRepo.Setup(r => r.GetByIdAsync(OtherSlotId)).ReturnsAsync(MakeSlot(DateTime.UtcNow.AddDays(3), SlotStatus.AVAILABLE, OtherSlotId));

        var request = new UpdateReservationDto { SlotId = OtherSlotId, Notes = "new time" };
        var result = await _service.UpdateReservationAsync("res-1", ProsumerNic, "PROSUMER", request);

        Assert.True(result.Success);
        Assert.Equal(ReservationStatus.PENDING, result.Reservation!.Status);
    }

    // A prosumer cannot change somebody else's booking.
    [Fact]
    public async Task UpdateReservation_ByAnotherProsumer_IsRejected()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.PENDING, nic: OtherNic);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);

        var request = new UpdateReservationDto { SlotId = OtherSlotId, Notes = "hack" };
        var result = await _service.UpdateReservationAsync("res-1", ProsumerNic, "PROSUMER", request);

        Assert.False(result.Success);
        Assert.Contains("unauthorized", result.Message);
    }

    // A COMPLETED booking can no longer be changed.
    [Fact]
    public async Task UpdateReservation_CompletedBooking_IsRejected()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.COMPLETED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);

        var request = new UpdateReservationDto { SlotId = OtherSlotId, Notes = "late change" };
        var result = await _service.UpdateReservationAsync("res-1", ProsumerNic, "PROSUMER", request);

        Assert.False(result.Success);
        Assert.Contains("Only PENDING or APPROVED", result.Message);
    }

    // ------------------------------------------------------------------
    // CANCEL reservation - 12-hour rule, ownership, status
    // ------------------------------------------------------------------

    // More than 12 hours before the start, cancelling works and the slot becomes AVAILABLE again.
    [Fact]
    public async Task CancelReservation_MoreThan12Hours_CancelsAndFreesSlot()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.APPROVED);
        var slot = MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.RESERVED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.CancelReservationAsync("res-1", ProsumerNic, "PROSUMER");

        Assert.True(result.Success);
        Assert.Equal(ReservationStatus.CANCELLED, reservation.Status);
        Assert.Equal(SlotStatus.AVAILABLE, slot.Status);
        _reservationRepo.Verify(r => r.UpdateAsync("res-1", reservation), Times.Once);
    }

    // Less than 12 hours before the start, cancelling is blocked.
    [Fact]
    public async Task CancelReservation_Within12Hours_IsRejected()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddHours(5), ReservationStatus.APPROVED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);

        var result = await _service.CancelReservationAsync("res-1", ProsumerNic, "PROSUMER");

        Assert.False(result.Success);
        Assert.Contains("12 hours", result.Message);
        Assert.Equal(ReservationStatus.APPROVED, reservation.Status);
    }

    // A grid operator may cancel a prosumer's booking on their behalf.
    [Fact]
    public async Task CancelReservation_ByGridOperator_IsAllowed()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.PENDING, nic: OtherNic);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.PENDING));

        var result = await _service.CancelReservationAsync("res-1", "operator-nic", "GRID_OPERATOR");

        Assert.True(result.Success);
        Assert.Equal(ReservationStatus.CANCELLED, reservation.Status);
    }

    // A booking that is already cancelled cannot be cancelled again.
    [Fact]
    public async Task CancelReservation_AlreadyCancelled_IsRejected()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.CANCELLED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);

        var result = await _service.CancelReservationAsync("res-1", ProsumerNic, "PROSUMER");

        Assert.False(result.Success);
    }

    // ------------------------------------------------------------------
    // STATUS changes by operator / backoffice
    // ------------------------------------------------------------------

    // Cancelling must go through the cancel method (which checks the 12-hour rule), not the status method.
    [Fact]
    public async Task UpdateStatus_ToCancelled_IsRejected()
    {
        var result = await _service.UpdateReservationStatusAsync("res-1", ReservationStatus.CANCELLED);

        Assert.False(result.Success);
        _reservationRepo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<EnergyReservation>()), Times.Never);
    }

    // Approving a booking reserves its slot.
    [Fact]
    public async Task UpdateStatus_ToApproved_ReservesSlot()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(2), ReservationStatus.PENDING);
        var slot = MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.PENDING);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.UpdateReservationStatusAsync("res-1", ReservationStatus.APPROVED);

        Assert.True(result.Success);
        Assert.Equal(ReservationStatus.APPROVED, result.Reservation!.Status);
        Assert.Equal(SlotStatus.RESERVED, slot.Status);
    }

    // Completing a booking (after the QR scan) frees the slot and records the completion time.
    [Fact]
    public async Task UpdateStatus_ToCompleted_ReleasesSlotAndSetsCompletedAt()
    {
        var reservation = MakeReservation(DateTime.UtcNow.AddDays(1), ReservationStatus.APPROVED);
        var slot = MakeSlot(reservation.ScheduledStartDateTime, SlotStatus.RESERVED);
        _reservationRepo.Setup(r => r.GetByIdAsync("res-1")).ReturnsAsync(reservation);
        _slotRepo.Setup(r => r.GetByIdAsync(SlotId)).ReturnsAsync(slot);

        var result = await _service.UpdateReservationStatusAsync("res-1", ReservationStatus.COMPLETED);

        Assert.True(result.Success);
        Assert.Equal(ReservationStatus.COMPLETED, reservation.Status);
        Assert.NotNull(reservation.CompletedAt);
        Assert.Equal(SlotStatus.AVAILABLE, slot.Status);
    }

    // ------------------------------------------------------------------
    // BOOKING VIEWS for the prosumer dashboard
    // ------------------------------------------------------------------

    // The pending list shows only this prosumer's PENDING bookings.
    [Fact]
    public async Task GetPendingReservations_ReturnsOnlyPendingBookingsOfThatProsumer()
    {
        var future = DateTime.UtcNow.AddDays(2);
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>
        {
            MakeReservation(future, ReservationStatus.PENDING, id: "mine-pending"),
            MakeReservation(future, ReservationStatus.APPROVED, id: "mine-approved"),
            MakeReservation(future, ReservationStatus.PENDING, nic: OtherNic, id: "other-pending")
        });

        var result = (await _service.GetPendingReservationsByNicAsync(ProsumerNic)).ToList();

        Assert.Single(result);
        Assert.Equal("mine-pending", result[0].ReservationId);
    }

    // The current list shows only APPROVED bookings that have not started yet.
    [Fact]
    public async Task GetCurrentReservations_ReturnsOnlyApprovedFutureBookings()
    {
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>
        {
            MakeReservation(DateTime.UtcNow.AddDays(1), ReservationStatus.APPROVED, id: "approved-future"),
            MakeReservation(DateTime.UtcNow.AddDays(-1), ReservationStatus.APPROVED, id: "approved-past"),
            MakeReservation(DateTime.UtcNow.AddDays(1), ReservationStatus.PENDING, id: "pending-future")
        });

        var result = (await _service.GetCurrentReservationsByNicAsync(ProsumerNic)).ToList();

        Assert.Single(result);
        Assert.Equal("approved-future", result[0].ReservationId);
    }
}
