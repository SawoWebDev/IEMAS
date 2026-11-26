namespace Iemas.Application.Employees.Dtos;

public record EmployeeDto(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? SupervisorEmployeeId,
    string? SupervisorEmployeeName,
    Guid? ManagerEmployeeId,
    string? ManagerEmployeeName,
    bool IsManagement = false);

public record CreateEmployeeRequest(
    string FullName,
    string Email,
    Guid? DepartmentId,
    Guid? SupervisorEmployeeId,
    bool IsManagement = false);

public record UpdateEmployeeRequest(
    string FullName,
    string Email,
    bool IsActive,
    Guid? DepartmentId,
    Guid? SupervisorEmployeeId,
    bool IsManagement = false);
