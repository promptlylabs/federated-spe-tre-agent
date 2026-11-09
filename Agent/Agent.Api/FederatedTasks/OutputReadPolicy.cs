using System.Text.Json;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Builds the RustFS canned policy that lets a TRE custodian read federated task result files
    /// (and nothing else) through the object store console.
    /// </summary>
    public static class OutputReadPolicy
    {
        /// <summary>
        /// Policy JSON: list and read access scoped to the output bucket. No write, no delete.
        /// </summary>
        public static string Build(string bucket)
        {
            var policy = new
            {
                Version = "2012-10-17",
                Statement = new object[]
                {
                    new
                    {
                        Sid = "SeeBuckets",
                        Effect = "Allow",
                        Action = new[] { "s3:ListAllMyBuckets", "s3:GetBucketLocation", "s3:GetBucketPolicyStatus" },
                        Resource = new[] { "arn:aws:s3:::*" }
                    },
                    new
                    {
                        Sid = "ListOutputs",
                        Effect = "Allow",
                        Action = new[] { "s3:ListBucket" },
                        Resource = new[] { $"arn:aws:s3:::{bucket}" }
                    },
                    new
                    {
                        Sid = "ReadOutputs",
                        Effect = "Allow",
                        Action = new[] { "s3:GetObject" },
                        Resource = new[] { $"arn:aws:s3:::{bucket}/*" }
                    }
                }
            };

            return JsonSerializer.Serialize(policy);
        }
    }
}
