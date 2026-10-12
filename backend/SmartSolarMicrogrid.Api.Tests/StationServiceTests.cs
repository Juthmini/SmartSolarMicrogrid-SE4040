/*
 * File Name: StationServiceTests.cs
 * Project: Smart Solar Microgrid Trading System
 * Module: SE4040 Enterprise Application Development
 * Author: IT22124708
 * Description: Unit tests for the microgrid node (station) business rules in
 *              StationService: a station cannot be deactivated or deleted while
 *              it still has active (PENDING or APPROVED) reservations.
 * Date: 2026-10-12
 */

using Moq;
using SmartSolarMicrogrid.Api.Models.DTOs.Stations;
using SmartSolarMicrogrid.Api.Models.Entities.Reservations;
using SmartSolarMicrogrid.Api.Models.Entities.Stations;
using SmartSolarMicrogrid.Api.Models.Enums.Reservations;
using SmartSolarMicrogrid.Api.Models.Enums.Stations;
using SmartSolarMicrogrid.Api.Repositories.Reservations;
using SmartSolarMicrogrid.Api.Repositories.Stations;
using SmartSolarMicrogrid.Api.Services.Stations;

namespace SmartSolarMicrogrid.Api.Tests;

public class StationServiceTests
{
    private readonly Mock<ISolarStationInfoRepository> _stationRepo = new();
    private readonly Mock<IEnergyReservationRepository> _reservationRepo = new();
    private readonly StationService _service;

    private const string StationId = "station-1";

    // Runs before every test: builds the service with fake repositories.
    public StationServiceTests()
    {
        _service = new StationService(_stationRepo.Object, _reservationRepo.Object);
    }

    // Builds an ACTIVE station.
    private static SolarStationInfo MakeActiveStation() => new()
    {
        StationId = StationId,
        StationName = "Colombo Solar Hub",
        Address = "Colombo",
        Capacity = 500,
        BatterySlotCount = 9,
        OperatingStartTime = "08:00",
        OperatingEndTime = "18:00",
        Status = StationStatus.ACTIVE
    };

    // Builds one APPROVED reservation for the station (counts as "active").
    private static List<EnergyReservation> OneActiveReservation() => new()
    {
        new EnergyReservation
        {
            ReservationId = "res-1",
            ReservationNumber = "RES-11111",
            ProsumerNic = "200012345678",
            StationId = StationId,
            SlotId = "slot-1",
            ScheduledStartDateTime = DateTime.UtcNow.AddDays(1),
            ScheduledEndDateTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            Status = ReservationStatus.APPROVED,
            QrReference = "qr-1"
        }
    };

    // Deactivation is blocked while the station has active reservations.
    [Fact]
    public async Task DeactivateStation_WithActiveReservations_IsBlocked()
    {
        var station = MakeActiveStation();
        _stationRepo.Setup(r => r.GetByIdAsync(StationId)).ReturnsAsync(station);
        _reservationRepo.Setup(r => r.GetActiveReservationsByStationIdAsync(StationId)).ReturnsAsync(OneActiveReservation());

        var result = await _service.DeactivateStationAsync(StationId);

        Assert.False(result.Success);
        Assert.Contains("active reservations", result.Message);
        Assert.Equal(StationStatus.ACTIVE, station.Status);
        _stationRepo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<SolarStationInfo>()), Times.Never);
    }

    // With no active reservations, the station is deactivated.
    [Fact]
    public async Task DeactivateStation_WithoutActiveReservations_Succeeds()
    {
        _stationRepo.Setup(r => r.GetByIdAsync(StationId)).ReturnsAsync(MakeActiveStation());
        _reservationRepo.Setup(r => r.GetActiveReservationsByStationIdAsync(StationId)).ReturnsAsync(new List<EnergyReservation>());

        var result = await _service.DeactivateStationAsync(StationId);

        Assert.True(result.Success);
        Assert.Equal(StationStatus.DEACTIVATED, result.Station!.Status);
        _stationRepo.Verify(r => r.UpdateAsync(StationId, It.IsAny<SolarStationInfo>()), Times.Once);
    }

    // The same rule applies when deactivating through the edit (update) form.
    [Fact]
    public async Task UpdateStation_SetDeactivatedWithActiveReservations_IsBlocked()
    {
        _stationRepo.Setup(r => r.GetByIdAsync(StationId)).ReturnsAsync(MakeActiveStation());
        _reservationRepo.Setup(r => r.GetActiveReservationsByStationIdAsync(StationId)).ReturnsAsync(OneActiveReservation());

        var request = new UpdateStationDto
        {
            StationName = "Colombo Solar Hub",
            Address = "Colombo",
            Capacity = 500,
            BatterySlotCount = 9,
            OperatingStartTime = "08:00",
            OperatingEndTime = "18:00",
            Status = StationStatus.DEACTIVATED
        };

        var result = await _service.UpdateStationAsync(StationId, request);

        Assert.False(result.Success);
        _stationRepo.Verify(r => r.UpdateAsync(It.IsAny<string>(), It.IsAny<SolarStationInfo>()), Times.Never);
    }

    // Deleting a station with active reservations throws an error and deletes nothing.
    [Fact]
    public async Task DeleteStation_WithActiveReservations_Throws()
    {
        _stationRepo.Setup(r => r.GetByIdAsync(StationId)).ReturnsAsync(MakeActiveStation());
        _reservationRepo.Setup(r => r.GetActiveReservationsByStationIdAsync(StationId)).ReturnsAsync(OneActiveReservation());

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteStationAsync(StationId));
        _stationRepo.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
    }
}
