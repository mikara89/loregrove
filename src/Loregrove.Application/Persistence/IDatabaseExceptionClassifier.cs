namespace Loregrove.Application.Persistence;

public interface IDatabaseExceptionClassifier
{
    bool IsUniqueConstraintViolation(Exception exception);
}
