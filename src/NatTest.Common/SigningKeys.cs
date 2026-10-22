using System;
using System.IO;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace NatTest.Common
{
    // ES256 (ECDSA P-256 + SHA-256) keys and signatures. BouncyCastle is used instead of System.Security.Cryptography.ECDsa
    // because Unity's Mono class libraries throw NotImplementedException from ECDsa.Create.
    public static class SigningKeys
    {
        public const int SignatureLength = 64;

        private static readonly ECDomainParameters P256 = new ECDomainParameters(ECNamedCurveTable.GetByOid(SecObjectIdentifiers.SecP256r1));

        // Public keys are PEM "PUBLIC KEY" (SubjectPublicKeyInfo), as written by MatchmakingService and introducer generate-key.
        public static ECPublicKeyParameters LoadPublicKeyPem(string pem, string name)
        {
            object key = ReadPem(pem, name);
            var publicKey = key as ECPublicKeyParameters;
            if (publicKey == null || !IsP256(publicKey.Parameters))
                throw new InvalidOperationException(name + " must be a PEM-encoded ECDSA P-256 public key.");
            return publicKey;
        }

        // Private keys are PEM "PRIVATE KEY" (PKCS#8) or "EC PRIVATE KEY" (SEC1).
        public static ECPrivateKeyParameters LoadPrivateKeyPem(string pem, string name)
        {
            object key = ReadPem(pem, name);
            var pair = key as AsymmetricCipherKeyPair;
            var privateKey = (pair != null ? pair.Private : key) as ECPrivateKeyParameters;
            if (privateKey == null || !IsP256(privateKey.Parameters))
                throw new InvalidOperationException(name + " must be a PEM-encoded ECDSA P-256 private key.");
            return privateKey;
        }

        public static ECPublicKeyParameters LoadPublicKeyFile(string path, string name) { return LoadPublicKeyPem(ReadFile(path, name), name); }

        public static ECPrivateKeyParameters LoadPrivateKeyFile(string path, string name) { return LoadPrivateKeyPem(ReadFile(path, name), name); }

        public static AsymmetricCipherKeyPair Generate()
        {
            var generator = new ECKeyPairGenerator();
            generator.Init(new ECKeyGenerationParameters(SecObjectIdentifiers.SecP256r1, new SecureRandom()));
            return generator.GenerateKeyPair();
        }

        public static ECPublicKeyParameters GetPublicKey(ECPrivateKeyParameters privateKey)
        {
            return new ECPublicKeyParameters(privateKey.Parameters.G.Multiply(privateKey.D).Normalize(), privateKey.Parameters);
        }

        public static string ToPem(AsymmetricKeyParameter key)
        {
            using (var writer = new StringWriter())
            {
                var pem = new PemWriter(writer);
                pem.WriteObject(key.IsPrivate ? (object)new Pkcs8Generator(key) : key);
                pem.Writer.Flush();
                return writer.ToString();
            }
        }

        // Returns the 64-byte raw R || S (IEEE P1363) signature. Nonces are deterministic (RFC 6979).
        public static byte[] Sign(ECPrivateKeyParameters key, byte[] data, int offset, int count)
        {
            var signer = new DsaDigestSigner(new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest())), new Sha256Digest(), PlainDsaEncoding.Instance);
            signer.Init(true, key);
            signer.BlockUpdate(data, offset, count);
            byte[] signature = signer.GenerateSignature();
            if (signature.Length != SignatureLength) throw new InvalidOperationException("Unexpected ES256 signature length.");
            return signature;
        }

        public static bool Verify(ECPublicKeyParameters key, byte[] data, int offset, int count, byte[] signature, int signatureOffset)
        {
            if (signature.Length - signatureOffset < SignatureLength) return false;
            var raw = new byte[SignatureLength];
            Buffer.BlockCopy(signature, signatureOffset, raw, 0, SignatureLength);
            try
            {
                var verifier = new DsaDigestSigner(new ECDsaSigner(), new Sha256Digest(), PlainDsaEncoding.Instance);
                verifier.Init(false, key);
                verifier.BlockUpdate(data, offset, count);
                return verifier.VerifySignature(raw);
            }
            catch (ArgumentException) { return false; }
        }

        private static bool IsP256(ECDomainParameters parameters)
        {
            return parameters != null && parameters.Curve.Equals(P256.Curve) && parameters.G.Equals(P256.G) && parameters.N.Equals(P256.N);
        }

        private static object ReadPem(string pem, string name)
        {
            try
            {
                using (var reader = new StringReader(pem ?? ""))
                {
                    object key = new PemReader(reader).ReadObject();
                    if (key != null) return key;
                }
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is InvalidCastException || exception is PemException)
            {
                // Fall through to the generic message: never echo key material.
            }
            throw new InvalidOperationException(name + " is not a valid PEM-encoded ECDSA P-256 key.");
        }

        private static string ReadFile(string path, string name)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException(name + " path is required.");
            if (!File.Exists(path)) throw new InvalidOperationException(name + " file does not exist: " + Path.GetFullPath(path));
            return File.ReadAllText(path);
        }
    }
}
