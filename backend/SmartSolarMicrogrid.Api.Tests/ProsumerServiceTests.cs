/*
 * File Name: ProsumerServiceTests.cs
 * Project: Smart Solar Microgrid Trading System
 * Module: SE4040 Enterprise Application Development
 * Author: IT22124708
 * Description: Unit tests for the prosumer account business rules in ProsumerService:
 *              NIC is the unique key, new accounts start as PENDING, a prosumer with
 *              ongoing bookings cannot deactivate, and only DEACTIVATED accounts can
 *              be reactivated.
 * Date: 2026-10-12
 */

using System.Linq.Expressions;
using Moq;
using SmartSolarMicrogrid.Api.Models.DTOs.Users;
using SmartSolarMicrogrid.Api.Models.Entities.Reservations;
using SmartSolarMicrogrid.Api.Models.Entities.Users;
using SmartSolarMicrogrid.Api.Models.Enums.Reservations;
using SmartSolarMicrogrid.Api.Models.Enums.Users;
using SmartSolarMicrogrid.Api.Repositories.Reservations;
using SmartSolarMicrogrid.Api.Repositories.Users;
using SmartSolarMicrogrid.Api.Services.Users;

namespace SmartSolarMicrogrid.Api.Tests;

public class ProsumerServiceTests
{
    private readonly Mock<IUserDetailsRepository> _userRepo = new();
    private readonly Mock<IEnergyReservationRepository> _reservationRepo = new();
    private readonly ProsumerService _service;

    private const string Nic = "200012345678";

    // Runs before every test: builds the service and sets safe default answers for the fakes.
    public ProsumerServiceTests()
    {
        _service = new ProsumerService(_userRepo.Object, _reservationRepo.Object);

        // By default nobody with this NIC, email or phone exists yet.
        _userRepo.Setup(r => r.GetByNicAsync(It.IsAny<string>())).ReturnsAsync((UserDetail?)null);
        _userRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((UserDetail?)null);
        _userRepo.Setup(r => r.FindAsync(It.IsAny<Expression<Func<UserDetail, bool>>>())).ReturnsAsync(new List<UserDetail>());
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>());
    }

    // Builds the registration form a new prosumer submits in the mobile app.
    private static RegisterProsumerDto MakeRegisterRequest() => new()
    {
        Nic = Nic,
        FullName = "Test Prosumer",
        Email = "prosumer@test.com",
        Phone = "0771234567",
        Address = "Colombo",
        Password = "secret123"
    };

    // Builds a prosumer account with the given status.
    private static UserDetail MakeProsumer(AccountStatus status) => new()
    {
        UserId = "user-1",
        Nic = Nic,
        FullName = "Test Prosumer",
        Email = "prosumer@test.com",
        Phone = "0771234567",
        PasswordHash = "hash",
        Role = UserRole.PROSUMER,
        AccountStatus = status,
        Address = "Colombo"
    };

    // A new NIC registers successfully, as a PROSUMER in PENDING status, with a hashed password.
    [Fact]
    public async Task Register_NewNic_CreatesPendingProsumer()
    {
        var result = await _service.RegisterProsumerAsync(MakeRegisterRequest());

        Assert.True(result.Success);
        Assert.Equal(AccountStatus.PENDING, result.Prosumer!.AccountStatus);
        Assert.Equal(UserRole.PROSUMER, result.Prosumer.Role);
        _userRepo.Verify(r => r.CreateAsync(It.Is<UserDetail>(u =>
            u.Nic == Nic && u.PasswordHash != "secret123")), Times.Once);
    }

    // NIC is the primary key: the same NIC cannot register twice.
    [Fact]
    public async Task Register_DuplicateNic_IsRejected()
    {
        _userRepo.Setup(r => r.GetByNicAsync(Nic)).ReturnsAsync(MakeProsumer(AccountStatus.ACTIVE));

        var result = await _service.RegisterProsumerAsync(MakeRegisterRequest());

        Assert.False(result.Success);
        Assert.Contains("NIC", result.Message);
        _userRepo.Verify(r => r.CreateAsync(It.IsAny<UserDetail>()), Times.Never);
    }

    // A prosumer with a PENDING or APPROVED booking cannot deactivate the account.
    [Fact]
    public async Task Deactivate_WithOngoingBookings_IsBlocked()
    {
        var user = MakeProsumer(AccountStatus.ACTIVE);
        _userRepo.Setup(r => r.GetByNicAsync(Nic)).ReturnsAsync(user);
        _reservationRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<EnergyReservation>
        {
            new EnergyReservation
            {
                ReservationId = "res-1",
                ReservationNumber = "RES-22222",
                ProsumerNic = Nic,
                StationId = "station-1",
                SlotId = "slot-1",
                Status = ReservationStatus.PENDING,
                QrReference = "qr-1"
            }
        });

        var result = await _service.DeactivateProsumerAsync(Nic);

        Assert.False(result.Success);
        Assert.Equal(AccountStatus.ACTIVE, user.AccountStatus);
    }

    // With no ongoing bookings, the prosumer can deactivate the account.
    [Fact]
    public async Task Deactivate_NoOngoingBookings_Succeeds()
    {
        _userRepo.Setup(r => r.GetByNicAsync(Nic)).ReturnsAsync(MakeProsumer(AccountStatus.ACTIVE));

        var result = await _service.DeactivateProsumerAsync(Nic);

        Assert.True(result.Success);
        Assert.Equal(AccountStatus.DEACTIVATED, result.Prosumer!.AccountStatus);
    }

    // A DEACTIVATED account can be reactivated (Backoffice action).
    [Fact]
    public async Task Reactivate_DeactivatedAccount_BecomesActive()
    {
        _userRepo.Setup(r => r.GetByNicAsync(Nic)).ReturnsAsync(MakeProsumer(AccountStatus.DEACTIVATED));

        var result = await _service.ReactivateProsumerAsync(Nic);

        Assert.NotNull(result);
        Assert.Equal(AccountStatus.ACTIVE, result!.AccountStatus);
    }

    // An account that is not DEACTIVATED cannot be "reactivated".
    [Fact]
    public async Task Reactivate_AccountThatIsNotDeactivated_ReturnsNull()
    {
        _userRepo.Setup(r => r.GetByNicAsync(Nic)).ReturnsAsync(MakeProsumer(AccountStatus.ACTIVE));

        var result = await _service.ReactivateProsumerAsync(Nic);

        Assert.Null(result);
    }
}
