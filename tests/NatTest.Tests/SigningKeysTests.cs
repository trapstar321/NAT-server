using System;
using System.IO;
using NatTest.Common;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Xunit;

namespace NatTest.Tests
{
    public sealed class SigningKeysTests
    {
        [Fact]
        public void Generated_keys_round_trip_through_pem_and_sign_verifiably()
        {
            var pair = SigningKeys.Generate();
            string privatePem = SigningKeys.ToPem(pair.Private);
            string publicPem = SigningKeys.ToPem(pair.Public);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", privatePem);
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", publicPem);

            var privateKey = SigningKeys.LoadPrivateKeyPem(privatePem, "test");
            var publicKey = SigningKeys.LoadPublicKeyPem(publicPem, "test");
            Assert.Equal(publicKey.Q, SigningKeys.GetPublicKey(privateKey).Q);

            var data = new byte[37];
            byte[] signature = SigningKeys.Sign(privateKey, data, 0, data.Length);
            Assert.Equal(64, signature.Length);
            Assert.True(SigningKeys.Verify(publicKey, data, 0, data.Length, signature, 0));
            data[0] = 1;
            Assert.False(SigningKeys.Verify(publicKey, data, 0, data.Length, signature, 0));
        }

        [Fact]
        public void Signing_is_deterministic()
        {
            var key = (ECPrivateKeyParameters)SigningKeys.Generate().Private;
            var data = new byte[] { 1, 2, 3 };
            Assert.Equal(SigningKeys.Sign(key, data, 0, 3), SigningKeys.Sign(key, data, 0, 3));
        }

        [Fact]
        public void Public_and_private_keys_cannot_be_swapped()
        {
            var pair = SigningKeys.Generate();
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPublicKeyPem(SigningKeys.ToPem(pair.Private), "test"));
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPrivateKeyPem(SigningKeys.ToPem(pair.Public), "test"));
        }

        [Fact]
        public void Keys_on_other_curves_are_rejected()
        {
            var generator = new ECKeyPairGenerator();
            generator.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP384r1, new SecureRandom()));
            var pair = generator.GenerateKeyPair();
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPublicKeyPem(SigningKeys.ToPem(pair.Public), "test"));
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPrivateKeyPem(SigningKeys.ToPem(pair.Private), "test"));
        }

        [Fact]
        public void Invalid_pem_fails_without_echoing_its_contents()
        {
            const string garbage = "-----BEGIN PUBLIC KEY-----\nc2VjcmV0LWlzaC1jb250ZW50\n-----END PUBLIC KEY-----\n";
            var exception = Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPublicKeyPem(garbage, "test"));
            Assert.DoesNotContain("c2VjcmV0", exception.Message);
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPrivateKeyPem("not a key", "test"));
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPublicKeyPem("", "test"));
        }

        [Fact]
        public void Missing_key_files_fail()
        {
            Assert.Throws<InvalidOperationException>(() => SigningKeys.LoadPublicKeyFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem"), "test"));
            Assert.Throws<ArgumentException>(() => SigningKeys.LoadPrivateKeyFile("", "test"));
        }
    }
}
